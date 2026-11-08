using System.Globalization;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Xunit;
using static W.Hangfire.Dameng.Tests.TestDatabase;

namespace W.Hangfire.Dameng.Tests;

public sealed partial class StorageTests
{
    [Fact]
    public async Task TwoServersProcessTheSameQueueAndRenewLongRunningWork()
    {
        var options = new DamengStorageOptions
        {
            QueuePollInterval = TimeSpan.FromMilliseconds(350),
            InvisibilityTimeout = TimeSpan.FromSeconds(36),
            LeaseRenewalInterval = TimeSpan.FromSeconds(9)
        };
        var storage = OpenWith(options);
        EnsureExecutionTable();
        var client = new BackgroundJobClient(storage);
        var queue = Queue();
        var longJob = client.Create(Job.FromExpression(() => Work.Record(queue, "long", 45000)), new EnqueuedState(queue));
        var jobs = Enumerable.Range(0, 5)
            .Select(i => client.Create(Job.FromExpression(() => Work.Record(queue, i.ToString(), 500)), new EnqueuedState(queue)))
            .Append(longJob).ToList();

        using var observer = OpenVerified();
        using var first = StartServer(storage, queue, 1, "first", lightweight: true);
        using var second = StartServer(OpenWith(options), queue, 1, "second", lightweight: true);
        var monitoring = storage.GetMonitoringApi();
        await Until(() => monitoring.Servers().Count(x => x.Queues.Contains(queue)) == 2);
        Assert.All(monitoring.Servers().Where(x => x.Queues.Contains(queue)), x =>
        {
            Assert.Equal(1, x.WorkersCount);
            Assert.NotNull(x.Heartbeat);
        });
        const string stateSql = "SELECT STATE_NAME FROM WHF_JOB WHERE ID = :p0";
        await Until(() => Equals(Scalar(observer, stateSql, Id(longJob)), ProcessingState.StateName), TimeSpan.FromSeconds(75));
        const string leaseSql = "SELECT LEASE_UNTIL FROM WHF_QUEUE WHERE JOB_ID = :p0";
        var initialLease = Assert.IsType<DateTime>(Scalar(observer, leaseSql, Id(longJob)));
        await Until(() => Scalar(observer, leaseSql, Id(longJob)) is DateTime current && current > initialLease);

        var ids = jobs.Select(id => (object)Id(id)).ToArray();
        var placeholders = string.Join(", ", ids.Select((_, i) => ":p" + i.ToString(CultureInfo.InvariantCulture)));
        var succeededSql = $"SELECT COUNT(*) FROM WHF_JOB WHERE STATE_NAME = 'Succeeded' AND ID IN ({placeholders})";
        await Until(() => Convert.ToInt64(Scalar(observer, succeededSql, ids), CultureInfo.InvariantCulture) == jobs.Count,
            TimeSpan.FromSeconds(150));

        Assert.Equal(6, Number("SELECT COUNT(*) FROM WHF_TEST_EXECUTION WHERE RUN_ID = :p0", queue));
        Assert.Equal(6, Number("SELECT COUNT(DISTINCT RESULT_VALUE) FROM WHF_TEST_EXECUTION WHERE RUN_ID = :p0", queue));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE QUEUE_NAME = :p0", queue));
        Assert.Equal(2, jobs.Select(id => monitoring.JobDetails(id).History.Single(h => h.StateName == "Processing").Data["ServerId"])
            .Distinct().Count());
        Assert.Contains(monitoring.SucceededJobs(0, 100), job => job.Key == longJob && job.Value.InSucceededState);
    }

    [Fact]
    public async Task RestartRunsDelayedRecurringAndContinuationJobsAndExposesMonitoring()
    {
        var store = Open();
        EnsureExecutionTable();
        var queue = Queue();
        var client = new BackgroundJobClient(store);
        var recurring = new RecurringJobManager(store);
        var delayed = client.Create(Job.FromExpression(() => Work.Record(queue, "delayed", 0), queue),
            new ScheduledState(TimeSpan.FromSeconds(4)));
        var antecedent = client.Create(Job.FromExpression(() => Work.Record(queue, "parent", 0)), new EnqueuedState(queue));
        var continuation = client.ContinueJobWith(antecedent, () => Work.Record(queue, "child", 0), new EnqueuedState(queue));
        var failure = client.Create(Job.FromExpression(() => Work.Fail()), new EnqueuedState(queue));
        // Schedule one real cron occurrence within the test window. The next
        // occurrence is a year later, so a slow run cannot accumulate extra work.
        DateTime firstOccurrence;
        using (var clock = (JobStorageConnection)store.GetConnection())
            firstOccurrence = clock.GetUtcDateTime().AddMinutes(1);
        var cron = firstOccurrence.ToString("ss mm HH dd MM", CultureInfo.InvariantCulture) + " *";
        recurring.AddOrUpdate(queue, Job.FromExpression(() => Work.Record(queue, "recurring", 0), queue), cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        try
        {
            var before = (JobStorageMonitor)Open().GetMonitoringApi();
            Assert.Contains(before.ScheduledJobs(0, 1000), x => x.Key == delayed && x.Value.InScheduledState);
            Assert.Contains(before.AwaitingJobs(0, 1000), x => x.Key == continuation && x.Value.ParentStateName == "Enqueued");
            Assert.Contains(before.Queues(), x => x.Name == queue && x.Length == 2);
            using (var connection = (JobStorageConnection)Open().GetConnection())
                Assert.True(connection.GetSetContains("recurring-jobs", queue));
            Assert.Equal("Scheduled", State(delayed));
            Assert.Equal("Awaiting", State(continuation));

            using (StartServer(OpenWith(new DamengStorageOptions()), queue, 1, "restart"))
            {
                await Until(() => new[] { delayed, antecedent, continuation }.All(x => State(x) == "Succeeded")
                    && State(failure) == "Failed"
                    && Number("SELECT COUNT(*) FROM WHF_TEST_EXECUTION WHERE RUN_ID = :p0 AND RESULT_VALUE = 'recurring'", queue) > 0,
                    TimeSpan.FromSeconds(180));
                recurring.RemoveIfExists(queue);
                await Until(() => before.EnqueuedCount(queue) == 0 && before.FetchedCount(queue) == 0);
            }

            foreach (var value in new[] { "delayed", "parent", "child", "recurring" })
                Assert.Equal(1, Number("SELECT COUNT(*) FROM WHF_TEST_EXECUTION WHERE RUN_ID = :p0 AND RESULT_VALUE = :p1", queue, value));
            Assert.Contains(before.FailedJobs(0, 100), x => x.Key == failure && x.Value.ExceptionType == "System.InvalidOperationException");
            Assert.Equal("Succeeded", before.JobDetails(continuation).History[0].StateName);
            Assert.Equal(7, before.SucceededByDatesCount().Count);
            Assert.Equal(24, before.HourlyFailedJobs().Count);
            Assert.True(before.FailedByDatesCount().Values.Sum() >= 1);
            Assert.True(before.GetStatistics().Succeeded >= 3);
        }
        finally
        {
            recurring.RemoveIfExists(queue);
        }
    }

    [Fact]
    public async Task ExpirationSweeperRemovesOwnedRowsAndRelatedHistory()
    {
        var storage = Open();
        var queue = Queue();
        var id = new BackgroundJobClient(storage).Create(Job.FromExpression(() => Work.Record(queue, "expired", 0)), new EnqueuedState(queue));
        using (var connection = storage.GetConnection())
        using (var transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            transaction.ExpireJob(id, TimeSpan.FromMilliseconds(250));
            transaction.AddToSet(queue, "value");
            transaction.ExpireSet(queue, TimeSpan.FromMilliseconds(250));
            transaction.IncrementCounter(queue, TimeSpan.FromMilliseconds(250));
            transaction.Commit();
        }

        using var server = StartServer(storage, Queue(), 1, "expiration");
        await Until(() => Number("SELECT COUNT(*) FROM WHF_JOB WHERE ID = :p0", Id(id)) == 0);
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_STATE WHERE JOB_ID = :p0", Id(id)));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(id)));
        using var read = (JobStorageConnection)storage.GetConnection();
        Assert.Empty(read.GetAllItemsFromSet(queue));
        Assert.Equal(0, read.GetCounter(queue));
    }

    private static void EnsureExecutionTable()
    {
        if (Number("SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = 'WHF_TEST_EXECUTION'") == 0)
            Execute("""
                CREATE TABLE WHF_TEST_EXECUTION (
                  ID BIGINT IDENTITY(1, 1) PRIMARY KEY, RUN_ID VARCHAR(64) NOT NULL, RESULT_VALUE VARCHAR(128) NOT NULL
                )
                """);
    }

    private static string? State(string id) => Scalar("SELECT STATE_NAME FROM WHF_JOB WHERE ID = :p0", Id(id)) as string;

    private static BackgroundJobServer StartServer(DamengStorage storage, string queue, int workers, string name, bool lightweight = false) =>
        new(new BackgroundJobServerOptions
        {
            ServerName = name, Queues = [queue], WorkerCount = workers, ShutdownTimeout = TimeSpan.FromSeconds(30),
            IsLightweightServer = lightweight, SchedulePollingInterval = TimeSpan.FromSeconds(2),
            HeartbeatInterval = TimeSpan.FromSeconds(30)
        }, storage);

    private static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(45));
        while (!condition())
        {
            try { await Task.Delay(200, cancellation.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("The live Dameng condition was not reached."); }
        }
    }
}
