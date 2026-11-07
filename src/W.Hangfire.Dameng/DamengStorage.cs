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
        public void Execute(BackgroundProcessContext context)
        {
            storage.Database.Write(session =>
            {
                var now = session.UtcNow();
                foreach (var table in new[] { "WHF_JOB", "WHF_COUNTER" })
                {
                    session.Execute($"""
                        DELETE FROM {table} WHERE ID IN (
                            SELECT ID FROM {table} WHERE EXPIRES_UTC <= :p0 ORDER BY EXPIRES_UTC LIMIT 200)
                        """, now);
                }

                session.Execute("""
                    DELETE FROM WHF_COLLECTION WHERE EXPIRES_UTC <= :p0 AND ROWNUM <= 200
                    """, now);
            });
            context.Wait(storage.Options.ExpirationCheckInterval);
        }
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
