using System.Diagnostics;
using Hangfire;
using Hangfire.Server;
using Hangfire.Storage;

namespace W.Hangfire.Dameng;

public sealed class DamengStorage : JobStorage
{
    private static readonly HashSet<string> Features =
    [
        JobStorageFeatures.JobQueueProperty,
        JobStorageFeatures.ExtendedApi,
        JobStorageFeatures.ProcessesInsteadOfComponents,
        JobStorageFeatures.Connection.GetUtcDateTime,
        JobStorageFeatures.Connection.GetSetContains,
        JobStorageFeatures.Connection.LimitedGetSetCount,
        JobStorageFeatures.Connection.BatchedGetFirstByLowest,
        JobStorageFeatures.Transaction.CreateJob,
        JobStorageFeatures.Transaction.SetJobParameter,
        JobStorageFeatures.Transaction.RemoveFromQueue(typeof(DamengFetchedJob)),
        JobStorageFeatures.Monitoring.AwaitingJobs,
        JobStorageFeatures.Monitoring.DeletedStateGraphs
    ];

    internal DamengDatabase Database { get; }
    internal DamengStorageOptions Options { get; }

    public DamengStorage(string connectionString, DamengStorageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Options = options ?? new DamengStorageOptions();
        Options.Validate();
        Database = new DamengDatabase(connectionString);
        DamengSchema.Ensure(Database, Options.SchemaLockTimeout);
    }

    public override bool LinearizableReads => true;
    public override bool HasFeature(string featureId) => Features.Contains(featureId) || base.HasFeature(featureId);
    public override IStorageConnection GetConnection() => new DamengStorageConnection(this);
    public override IMonitoringApi GetMonitoringApi() => new DamengMonitoringApi(Database);
    public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses() => [new ExpirationProcess(this)];

    private sealed class ExpirationProcess(DamengStorage storage) : IBackgroundProcess
    {
        private const int BatchSize = 200;
        private const int MaxBatches = 8;
        private static readonly TimeSpan CycleBudget = TimeSpan.FromSeconds(25);

        public void Execute(BackgroundProcessContext context)
        {
            var elapsed = Stopwatch.StartNew();
            for (var batch = 0; batch < MaxBatches; batch++)
            {
                context.StoppingToken.ThrowIfCancellationRequested();
                var fullBatch = storage.Database.Write(session =>
                {
                    context.StoppingToken.ThrowIfCancellationRequested();
                    var now = session.UtcNow();
                    var jobs = DeleteExpiredRows(session, "WHF_JOB", now);
                    var counters = DeleteExpiredRows(session, "WHF_COUNTER", now);
                    var collections = session.Execute($"""
                        DELETE FROM WHF_COLLECTION WHERE EXPIRES_UTC <= :p0 AND ROWNUM <= {BatchSize}
                        """, now);
                    return jobs == BatchSize || counters == BatchSize || collections == BatchSize;
                });

                // The transaction and WRITER lock have been released before starting the next batch.
                if (!fullBatch || elapsed.Elapsed >= CycleBudget) break;
            }

            context.Wait(storage.Options.ExpirationCheckInterval);
        }

        private static int DeleteExpiredRows(SqlSession session, string table, DateTime now) => session.Execute($"""
            DELETE FROM {table} WHERE ID IN (
                SELECT ID FROM {table} WHERE EXPIRES_UTC <= :p0 ORDER BY EXPIRES_UTC, ID LIMIT {BatchSize})
            """, now);

    }
}

public static class DamengStorageExtensions
{
    public static IGlobalConfiguration<DamengStorage> UseDamengStorage(
        this IGlobalConfiguration configuration, string connectionString, DamengStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.UseStorage(new DamengStorage(connectionString, options));
    }
}
