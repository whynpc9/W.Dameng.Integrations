using System.Data;
using System.Diagnostics;
using Hangfire.Storage;
using W.Dm;

namespace W.Hangfire.Dameng;

internal sealed class DamengMutex(DmConnection connection, DmTransaction transaction) : IDisposable
{
    private readonly SqlSession _session = new(connection, transaction);

    internal static void EnsureRow(SqlSession session, string key)
    {
        if (session.Count("SELECT COUNT(*) FROM WHF_LOCK WHERE K = :p0", key) != 0) return;
        try { session.Execute("INSERT INTO WHF_LOCK (K) VALUES (:p0)", key); }
        catch (DmException ex) when (ex.Number == -6602) { } // Concurrent insert of the same key.
    }

    internal static DamengMutex Acquire(DamengDatabase database, string key, string resource, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var timer = Stopwatch.StartNew();
        // Keep the bootstrap insert separate from the locking session. A concurrent
        // insert conflict can invalidate that connection in W.DamengProvider.
        database.Read(s => { EnsureRow(s, key); return 0; });
        var connection = database.Open(dedicated: true);
        try
        {
            while (true)
            {
                var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted, false);
                try
                {
                    var session = new SqlSession(connection, transaction);
                    if (session.Scalar("SELECT K FROM WHF_LOCK WHERE K = :p0 FOR UPDATE 1 SKIP LOCKED", key) != null)
                        return new DamengMutex(connection, transaction);
                }
                catch
                {
                    transaction.Dispose();
                    throw;
                }

                transaction.Dispose();
                var remaining = timeout - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new DistributedLockTimeoutException(resource);
                Thread.Sleep(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25));
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void Verify() => _session.Scalar("SELECT 1 FROM DUAL");

    public void Dispose()
    {
        try { transaction.Dispose(); }
        finally { connection.Dispose(); }
    }
}
