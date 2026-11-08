using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using W.Dm;
using Xunit;
using static W.Hangfire.Dameng.Tests.TestDatabase;

namespace W.Hangfire.Dameng.Tests;

public sealed partial class StorageTests
{
    static StorageTests() => global::Hangfire.Logging.LogProvider.SetCurrentLogProvider(new TestLogProvider());

    [Fact]
    public async Task ConcurrentSchemaInitializationAndResumableUpgrade()
    {
        var stores = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Open)));
        Assert.Equal(2, Number("SELECT VERSION_NO FROM WHF_SCHEMA WHERE ID = 1"));

        var queue = Queue();
        var id = new BackgroundJobClient(stores[0]).Create(Job.FromExpression(() => Work.Record(queue, "upgrade")), new EnqueuedState(queue));
        Execute("DROP INDEX WHF_QUEUE_NAME_I");
        Execute("UPDATE WHF_SCHEMA SET VERSION_NO = 1 WHERE ID = 1");
        var reopened = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Open)));
        Assert.Equal(2, Number("SELECT VERSION_NO FROM WHF_SCHEMA WHERE ID = 1"));
        Assert.Equal(1, Number("SELECT COUNT(*) FROM USER_INDEXES WHERE INDEX_NAME = 'WHF_QUEUE_NAME_I'"));
        Assert.Equal("Enqueued", Scalar("SELECT STATE_NAME FROM WHF_JOB WHERE ID = :p0", Id(id)));
        Assert.Equal(id, Assert.Single(reopened[0].GetMonitoringApi().EnqueuedJobs(queue, 0, 5)).Key);
    }

    [Fact]
    public async Task InitialExpiryUsesTheProvidedCreationTime()
    {
        using var connection = (JobStorageConnection)Open().GetConnection();
        var created = connection.GetUtcDateTime().Date.AddDays(-3).AddHours(4);
        var retention = TimeSpan.FromDays(10);
        var job = Job.FromExpression(() => Work.Record("expiry", "probe"));

        var direct = connection.CreateExpiredJob(job, new Dictionary<string, string>(), created, retention);
        using var transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
        var buffered = transaction.CreateJob(job, new Dictionary<string, string>(), created.ToLocalTime(), retention);
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_JOB WHERE ID = :p0", Id(buffered)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        transaction.Commit();

        foreach (var id in new[] { direct, buffered })
        {
            Assert.Equal(created, Scalar("SELECT CREATED_UTC FROM WHF_JOB WHERE ID = :p0", Id(id)));
            Assert.Equal(created + retention, Scalar("SELECT EXPIRES_UTC FROM WHF_JOB WHERE ID = :p0", Id(id)));
        }

        var alreadyExpired = connection.CreateExpiredJob(job, new Dictionary<string, string>(), created, TimeSpan.FromHours(1));
        Assert.Equal(created.AddHours(1), Scalar("SELECT EXPIRES_UTC FROM WHF_JOB WHERE ID = :p0", Id(alreadyExpired)));
        Assert.Null(connection.GetJobData(alreadyExpired));
    }

    [Fact]
    public void RejectsDriftedCurrentSchemaBeforeStorageInitialization()
    {
        var allocation = Allocation();
        var alternate = "WHF_ALT_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        Execute($"CREATE SCHEMA {alternate}");
        try
        {
            var settings = allocation.Settings();
            settings.Schema = alternate;
            using (var independent = new DmConnection(settings.ConnectionString))
            {
                independent.Open();
                using var check = independent.CreateCommand("SELECT SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()) FROM DUAL");
                Assert.Equal(alternate, check.ExecuteScalar());
            }

            var drifted = allocation with { ConnectionString = settings.ConnectionString };
            var error = Assert.Throws<InvalidOperationException>(() => OpenWith(drifted, new DamengStorageOptions()));
            Assert.Contains("schema", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, Number("SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER = :p0", alternate));
        }
        finally
        {
            Execute($"DROP SCHEMA {alternate}");
        }
    }

    [Fact]
    public void CreationAndStateWritesAreAtomic()
    {
        var storage = Open();
        using var connection = (JobStorageConnection)storage.GetConnection();
        var queue = Queue();
        var job = Job.FromExpression(() => Work.Record(queue, "atomic"));

        string aborted;
        using (var tx = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            aborted = tx.CreateJob(job, new Dictionary<string, string> { ["probe"] = "uncommitted" }, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            tx.SetJobState(aborted, new EnqueuedState(queue));
            tx.AddToQueue(queue, aborted);
            tx.AddToQueue(queue, long.MaxValue.ToString());
            Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_JOB WHERE ID = :p0", Id(aborted)));
            Assert.Throws<DmException>(tx.Commit);
        }

        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_JOB WHERE ID = :p0", Id(aborted)));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_STATE WHERE JOB_ID = :p0", Id(aborted)));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_PARAM WHERE JOB_ID = :p0", Id(aborted)));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE QUEUE_NAME = :p0", queue));

        var id = new BackgroundJobClient(storage).Create(job, new EnqueuedState(queue));
        Assert.Equal("Enqueued", Scalar("SELECT STATE_NAME FROM WHF_JOB WHERE ID = :p0", Id(id)));
        Assert.Equal(1, Number("SELECT COUNT(*) FROM WHF_STATE WHERE JOB_ID = :p0", Id(id)));
        Assert.Equal(1, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(id)));
    }
}
