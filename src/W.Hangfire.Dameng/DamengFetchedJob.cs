using Hangfire.Logging;
using Hangfire.Storage;
using static W.Hangfire.Dameng.DamengDatabase;

namespace W.Hangfire.Dameng;

internal sealed class DamengFetchedJob : IFetchedJob
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(DamengFetchedJob));
    private readonly CancellationTokenSource _stop = new();
    private readonly DamengStorageConnection _owner;
    private readonly Task _renewal;
    private int _disposed;
    private int _acknowledged;

    internal DamengStorage Storage => _owner.Storage;
    internal long Id { get; }
    internal long NumericJobId { get; }
    private string Token { get; }
    public string JobId => DamengDatabase.Id(NumericJobId);

    internal DamengFetchedJob(DamengStorageConnection owner, long id, long jobId, string token)
    {
        _owner = owner;
        Id = id;
        NumericJobId = jobId;
        Token = token;
        _renewal = Task.Run(Renew);
    }

    internal void Verify(SqlSession session, DateTime now)
    {
        if (session.Count("SELECT COUNT(*) FROM WHF_QUEUE WHERE ID = :p0 AND LEASE_TOKEN = :p1 AND LEASE_UNTIL > :p2",
                Id, Token, now) != 1)
            throw new InvalidOperationException("The Hangfire queue lease has expired or changed ownership.");
    }

    internal void Acknowledge(SqlSession session) =>
        session.Execute("DELETE FROM WHF_QUEUE WHERE ID = :p0 AND LEASE_TOKEN = :p1", Id, Token);

    internal void MarkAcknowledged() => Volatile.Write(ref _acknowledged, 1);

    public void RemoveFromQueue()
    {
        if (Volatile.Read(ref _acknowledged) != 0) return;
        Storage.Database.Write(s => s.Execute("""
            DELETE FROM WHF_QUEUE WHERE ID = :p0 AND LEASE_TOKEN = :p1
              AND LEASE_UNTIL > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP())
            """, Id, Token));
        MarkAcknowledged();
    }

    public void Requeue() => Storage.Database.Write(s => s.Execute("""
        UPDATE WHF_QUEUE SET LEASE_TOKEN = NULL, FETCHED_UTC = NULL, LEASE_UNTIL = NULL
        WHERE ID = :p0 AND LEASE_TOKEN = :p1
          AND LEASE_UNTIL > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP())
        """, Id, Token));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _renewal.GetAwaiter().GetResult(); }
        finally
        {
            _stop.Dispose();
            _owner.Forget(this);
        }
    }

    private async Task Renew()
    {
        try
        {
            using var timer = new PeriodicTimer(Storage.Options.LeaseRenewalInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _acknowledged) != 0) return;
                try
                {
                    var renewed = Storage.Database.Write(s =>
                    {
                        var now = s.UtcNow();
                        return s.Execute("""
                            UPDATE WHF_QUEUE SET LEASE_UNTIL = :p0
                            WHERE ID = :p1 AND LEASE_TOKEN = :p2 AND LEASE_UNTIL > :p3
                            """, now + Storage.Options.InvisibilityTimeout, Id, Token, now);
                    });
                    if (renewed == 0) return;
                }
                catch (Exception ex) when (ex is W.Dm.DmException or InvalidOperationException or TimeoutException)
                {
                    Logger.Warn("Could not renew a Dameng Hangfire queue lease. Renewal will retry before expiry.");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
}

internal readonly record struct QueueClaim(long Id, long JobId, string Token)
{
    internal static QueueClaim? TryTake(DamengStorage storage, string[] queues, CancellationToken token)
    {
        var parameters = string.Join(", ", queues.Select((_, i) => ":p" + i));
        var sql = $"""
            SELECT ID, JOB_ID FROM WHF_QUEUE WHERE QUEUE_NAME IN ({parameters})
              AND (LEASE_UNTIL IS NULL OR LEASE_UNTIL <= SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))
            ORDER BY ID LIMIT 1
            """;
        var values = queues.Cast<object?>().ToArray();
        if (storage.Database.Read(s => s.Scalar(sql, values)) is null) return null;
        return storage.Database.Write<QueueClaim?>(s =>
        {
            token.ThrowIfCancellationRequested();
            var rows = s.Query(sql, r => (Id: r.GetInt64(0), JobId: r.GetInt64(1)), values);
            if (rows.Count == 0) return null;

            var row = rows[0];
            var leaseToken = Guid.NewGuid().ToString("N");
            var now = s.UtcNow();
            s.Execute("""
                UPDATE WHF_QUEUE SET LEASE_TOKEN = :p0, FETCHED_UTC = :p1, LEASE_UNTIL = :p2 WHERE ID = :p3
                """, leaseToken, now, now + storage.Options.InvisibilityTimeout, row.Id);
            return new QueueClaim(row.Id, row.JobId, leaseToken);
        });
    }
}
