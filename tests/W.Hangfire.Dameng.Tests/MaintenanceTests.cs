using System.Data;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using W.Dm;
using Xunit;
using static W.Hangfire.Dameng.Tests.TestDatabase;

namespace W.Hangfire.Dameng.Tests;

public sealed partial class StorageTests
{
    [Fact]
    public async Task ExpirationDrainsMultipleBatchesWhileClaimsHeartbeatsAndRenewalsProgress()
    {
        var storage = OpenWith(new DamengStorageOptions
        {
            ExpirationCheckInterval = TimeSpan.FromMinutes(2),
            InvisibilityTimeout = TimeSpan.FromSeconds(30),
            LeaseRenewalInterval = TimeSpan.FromSeconds(1)
        });
        var queue = Queue();
        var counter = Digest(queue);
        var liveId = new BackgroundJobClient(storage)
            .Create(Job.FromExpression(() => Work.Record(queue, "maintenance")), new EnqueuedState(queue));
        Execute("""
            INSERT INTO WHF_COUNTER (ID, K, DELTA, EXPIRES_UTC)
            SELECT WHF_SEQ.NEXTVAL, :p0, 1, TIMESTAMP '2000-01-01 00:00:00'
            FROM (SELECT LEVEL AS N FROM DUAL CONNECT BY LEVEL <= 1801) GENERATED_ROWS
            """, counter);
        long Backlog() => Number("SELECT COUNT(*) FROM WHF_COUNTER WHERE K = :p0", counter);
        Assert.Equal(1801, Backlog());

        using var server = storage.GetConnection();
        var serverId = Queue();
        server.AnnounceServer(serverId, new ServerContext { WorkerCount = 1, Queues = [queue] });
        var heartbeatBefore = storage.GetMonitoringApi().Servers().Single(x => x.Name == serverId).Heartbeat;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var process = Assert.Single(storage.GetStorageWideProcesses());
        var context = new BackgroundProcessContext(serverId, storage, new Dictionary<string, object>(), Guid.NewGuid(),
            cancellation.Token, cancellation.Token, cancellation.Token);
        // Block the first batch on an expired row while it holds WRITER. Queue
        // competing API calls before releasing it, then require another committed
        // cleanup batch after the claimant, heartbeat and lease renewal progressed.
        using var blocker = OpenVerified();
        using var blockerTransaction = blocker.BeginTransaction(IsolationLevel.ReadCommitted, false);
        using (var command = blocker.CreateCommand("SELECT ID FROM WHF_COUNTER WHERE ID = :p0 FOR UPDATE"))
        {
            command.Transaction = blockerTransaction;
            command.Parameters.Add(new DmParameter("p0", Number("SELECT MIN(ID) FROM WHF_COUNTER WHERE K = :p0", counter)));
            Assert.NotNull(command.ExecuteScalar());
        }

        var sweep = Task.Run(() => process.Execute(context));
        try
        {
            await Until(WriterIsHeld);
            Assert.Equal(1801, Backlog());
            using var worker = storage.GetConnection();
            var claiming = Task.Run(() => worker.FetchNextJob([queue], cancellation.Token));
            var heartbeating = Task.Run(() => server.Heartbeat(serverId));
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(claiming.IsCompleted);
            Assert.False(heartbeating.IsCompleted);
            blockerTransaction.Commit();

            using var lease = await claiming;
            await heartbeating;
            Assert.Equal(liveId, lease.JobId);
            var firstExpiry = Assert.IsType<DateTime>(Scalar("SELECT LEASE_UNTIL FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(liveId)));
            Assert.True(storage.GetMonitoringApi().Servers().Single(x => x.Name == serverId).Heartbeat > heartbeatBefore);
            await Until(() => Scalar("SELECT LEASE_UNTIL FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(liveId)) is DateTime next
                && next > firstExpiry);
            var afterProgress = Backlog();
            Assert.InRange(afterProgress, 202, 1601);
            await Until(() => Backlog() < afterProgress);
            lease.RemoveFromQueue();
            Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(liveId)));
        }
        finally
        {
            cancellation.Cancel();
            // On an assertion failure, release the gate before waiting for cleanup to stop.
            if (blockerTransaction.Outcome == DmTransactionOutcome.Active) blockerTransaction.Rollback();
            try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.WaitAsync(TimeSpan.FromSeconds(45))); }
            finally { server.RemoveServer(serverId); }
        }

        var remaining = Backlog();
        Assert.True(remaining > 0);
        Assert.Equal(remaining, Backlog());
        Execute("DELETE FROM WHF_COUNTER WHERE K = :p0", counter);
    }

    private static bool WriterIsHeld()
    {
        using var probe = OpenVerified();
        using var transaction = probe.BeginTransaction(IsolationLevel.ReadCommitted, false);
        using var command = probe.CreateCommand("SELECT K FROM WHF_LOCK WHERE K = 'WRITER' FOR UPDATE 1 SKIP LOCKED");
        command.Transaction = transaction;
        return command.ExecuteScalar() is null or DBNull;
    }

    [Fact]
    public void MonitoringBatchesQueuesParentsAndTimelineWithoutChangingDtos()
    {
        var storage = OpenWith(new DamengStorageOptions());
        var availableQueue = Queue();
        var fetchedOnlyQueue = Queue();
        var job = Job.FromExpression(() => Work.Record("monitor", "value"));
        var queued = new List<string>();
        var awaiting = new List<string>();
        using var connection = (JobStorageConnection)storage.GetConnection();
        var created = connection.GetUtcDateTime();
        var historicalHour = created.Date.AddHours(created.Hour).AddHours(-2);
        var metric = "w.hangfire:metrics:deleted:" + historicalHour.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
        const string LiveBucketTotal = """
            SELECT COALESCE(SUM(DELTA), 0) FROM WHF_COUNTER WHERE K = :p0
              AND (EXPIRES_UTC IS NULL OR EXPIRES_UTC > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))
            """;
        var priorTotal = Number(LiveBucketTotal, Digest(metric));
        using (var transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            for (var i = 0; i < 7; i++)
            {
                var id = transaction.CreateJob(job, new Dictionary<string, string>(), created, TimeSpan.FromHours(1));
                transaction.SetJobState(id, new EnqueuedState(availableQueue));
                transaction.AddToQueue(availableQueue, id);
                queued.Add(id);
            }
            transaction.AddToQueue(fetchedOnlyQueue, queued[^1]);

            foreach (var parent in new[] { queued[0], queued[0], long.MaxValue.ToString(CultureInfo.InvariantCulture), "invalid" })
            {
                var id = transaction.CreateJob(job, new Dictionary<string, string>(), created, TimeSpan.FromHours(1));
                transaction.SetJobState(id, new AwaitingState(parent));
                awaiting.Add(id);
            }

            transaction.IncrementCounter(metric);
            transaction.IncrementCounter(metric, TimeSpan.FromHours(1));
            transaction.IncrementCounter(metric, TimeSpan.FromSeconds(-1));
            transaction.Commit();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fetched = connection.FetchNextJob([fetchedOnlyQueue], timeout.Token);
        using var fetchedFromMixedQueue = connection.FetchNextJob([availableQueue], timeout.Token);
        Assert.Equal(queued[0], fetchedFromMixedQueue.JobId);
        var monitoring = (JobStorageMonitor)storage.GetMonitoringApi();

        var queues = monitoring.Queues();
        var available = Assert.Single(queues, x => x.Name == availableQueue);
        Assert.Equal(6, available.Length);
        Assert.Equal(1, available.Fetched);
        Assert.Equal(queued.Skip(1).Take(5), available.FirstJobs.Select(x => x.Key));
        Assert.All(available.FirstJobs, x =>
        {
            Assert.True(x.Value.InEnqueuedState);
            Assert.Equal("Enqueued", x.Value.State);
            Assert.Equal("value", x.Value.Job.Args[1]);
        });
        var fetchedOnly = Assert.Single(queues, x => x.Name == fetchedOnlyQueue);
        Assert.Equal(0, fetchedOnly.Length);
        Assert.Equal(1, fetchedOnly.Fetched);
        Assert.Empty(fetchedOnly.FirstJobs);
        Assert.Equal(7, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE QUEUE_NAME = :p0", availableQueue));

        var page = monitoring.AwaitingJobs(0, 1000).Where(row => awaiting.Contains(row.Key)).ToList();
        Assert.Equal(awaiting, page.Select(row => row.Key));
        Assert.Equal(new string?[] { "Enqueued", "Enqueued", null, null }, page.Select(row => row.Value.ParentStateName));

        // The driver also counts lower-level reads. Fewer than 24 read/execute
        // events rules out a separate command for every hourly bucket.
        using var operations = new QueryOperationCounter();
        var before = operations.Total();
        var timeline = monitoring.HourlyDeletedJobs();
        Assert.InRange(operations.Total() - before, 2, 23);
        Assert.Equal(24, timeline.Count);
        Assert.Equal(priorTotal + 2, timeline[historicalHour]);
        Assert.Contains(timeline, pair => pair.Value == 0);
        Assert.Equal(priorTotal + 2, Number(LiveBucketTotal, Digest(metric)));
        fetched.RemoveFromQueue();
        fetchedFromMixedQueue.RemoveFromQueue();
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class QueryOperationCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long? _value;

        internal QueryOperationCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DmDiagnostics.MeterName && instrument.Name == "wdm.operation.total")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "operation" && tag.Value is ("fetch" or "execute")) _value = (_value ?? 0) + value;
            });
            _listener.Start();
        }

        internal long Total()
        {
            _value = null;
            _listener.RecordObservableInstruments();
            return _value ?? throw new InvalidOperationException("WDriver operation counter is unavailable.");
        }

        public void Dispose() => _listener.Dispose();
    }
}
