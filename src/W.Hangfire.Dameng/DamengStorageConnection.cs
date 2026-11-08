using System.Diagnostics;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using static W.Hangfire.Dameng.DamengCollections;
using static W.Hangfire.Dameng.DamengDatabase;

namespace W.Hangfire.Dameng;

internal sealed class DamengStorageConnection(DamengStorage storage) : JobStorageConnection
{
    private readonly Dictionary<string, HeldLock> _locks = new(StringComparer.Ordinal);
    private readonly HashSet<DamengFetchedJob> _leases = [];
    private bool _disposed;

    internal DamengStorage Storage => storage;
    public override IWriteOnlyTransaction CreateWriteTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new DamengTransaction(this);
    }

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        if (!_locks.TryGetValue(resource, out var held))
        {
            var elapsed = Stopwatch.StartNew();
            var mutex = DamengMutex.Acquire(storage.Database, Key(resource), resource, timeout);
            try
            {
                // A previous owner can lose its resource session just before committing
                // on the writer session. Grant the lock only after that writer finishes.
                storage.Database.FenceLockAcquisition(mutex, resource, timeout - elapsed.Elapsed);

                held = new HeldLock(mutex);
                _locks.Add(resource, held);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }
        else
        {
            held.Mutex.Verify();
        }

        held.Count++;
        return new LockScope(() =>
        {
            if (--held.Count != 0 || !_locks.Remove(resource)) return;
            held.Mutex.Dispose();
        });
    }

    internal void VerifyLocks()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var held in _locks.Values) held.Mutex.Verify();
    }

    internal void VerifyLease(SqlSession session, long jobId, DateTime now, DamengFetchedJob? acknowledged)
    {
        foreach (var lease in _leases)
            if (lease != acknowledged && lease.NumericJobId == jobId) lease.Verify(session, now);
    }

    internal void Forget(DamengFetchedJob job) => _leases.Remove(job);

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception> errors = [];
        foreach (var lease in _leases.ToArray()) DisposeResource(lease, errors);
        foreach (var held in _locks.Values) DisposeResource(held.Mutex, errors);
        _locks.Clear();
        base.Dispose();
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    private static void DisposeResource(IDisposable resource, List<Exception> errors)
    {
        try { resource.Dispose(); }
        catch (Exception error) { errors.Add(error); }
    }

    public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        using var transaction = (DamengTransaction)CreateWriteTransaction();
        var id = transaction.CreateJob(job, parameters, createdAt, expireIn);
        transaction.Commit();
        return id;
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(queues);
        if (queues.Length == 0 || queues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one valid queue is required.", nameof(queues));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = QueueClaim.TryTake(storage, queues, cancellationToken);
            if (claim is { } job)
            {
                var fetched = new DamengFetchedJob(this, job.Id, job.JobId, job.Token);
                _leases.Add(fetched);
                return fetched;
            }
            cancellationToken.WaitHandle.WaitOne(storage.Options.QueuePollInterval);
        }
    }

    public override void SetJobParameter(string id, string name, string? value)
    {
        using var transaction = (DamengTransaction)CreateWriteTransaction();
        transaction.SetJobParameter(id, name, value);
        transaction.Commit();
    }

    public override string? GetJobParameter(string id, string name) =>
        Read(s => s.Query("SELECT CONTENT FROM WHF_PARAM WHERE JOB_ID = :p0 AND FIELD_KEY = :p1",
            r => SqlSession.Json<KeyValuePair<string, string?>>(r, 0).Value, JobId(id), Key(name)).SingleOrDefault());

    public override JobData? GetJobData(string jobId) => Read(s =>
    {
        var id = JobId(jobId);
        var data = s.Query($"""
            SELECT J.PAYLOAD, J.STATE_NAME, J.CREATED_UTC FROM WHF_JOB J WHERE J.ID = :p0 AND {DamengJobs.Active}
            """, r =>
        {
            var job = DamengJobs.Load(r.GetString(0));
            job.State = r.IsDBNull(1) ? null : r.GetString(1);
            job.CreatedAt = SqlSession.Utc(r, 2);
            return job;
        }, id).SingleOrDefault();
        if (data != null) data.ParametersSnapshot = DamengJobs.Parameters(s, id);
        return data;
    });

    public override StateData? GetStateData(string jobId) => Read(s => s.Query($"""
        SELECT S.STATE_NAME, S.CONTENT FROM WHF_JOB J JOIN WHF_STATE S ON S.ID = J.CURRENT_STATE_ID
        WHERE J.ID = :p0 AND {DamengJobs.Active}
        """, r =>
    {
        var content = SqlSession.Json<StateContent>(r, 1);
        return new StateData { Name = r.GetString(0), Reason = content.Reason, Data = content.Data };
    }, JobId(jobId)).SingleOrDefault());

    public override void AnnounceServer(string serverId, ServerContext context) => Write(s =>
    {
        var now = s.UtcNow();
        var key = Key(serverId);
        var content = Lob.Json(new ServerContent(serverId, context.WorkerCount, context.Queues));
        if (s.Execute("""
                UPDATE WHF_SERVER SET CONTENT = :p0, STARTED_UTC = :p1, HEARTBEAT_UTC = :p2 WHERE SERVER_KEY = :p3
                """, content, now, now, key) == 0)
            s.Execute("""
                INSERT INTO WHF_SERVER (SERVER_KEY, CONTENT, STARTED_UTC, HEARTBEAT_UTC) VALUES (:p0, :p1, :p2, :p3)
                """, key, content, now, now);
    });

    public override void RemoveServer(string serverId) =>
        Write(s => s.Execute("DELETE FROM WHF_SERVER WHERE SERVER_KEY = :p0", Key(serverId)));

    public override void Heartbeat(string serverId)
    {
        var updated = Write(s => s.Execute(
            "UPDATE WHF_SERVER SET HEARTBEAT_UTC = :p0 WHERE SERVER_KEY = :p1", s.UtcNow(), Key(serverId)));
        if (updated == 0) throw new BackgroundServerGoneException();
    }

    public override int RemoveTimedOutServers(TimeSpan timeOut)
    {
        if (timeOut < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeOut));
        return Write(s => s.Execute("DELETE FROM WHF_SERVER WHERE HEARTBEAT_UTC < :p0", s.UtcNow() - timeOut));
    }

    public override HashSet<string> GetAllItemsFromSet(string key) =>
        new(ReadItems(Set, key, "ORDER BY I.SCORE, I.ID"), StringComparer.Ordinal);

    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) =>
        GetFirstByLowestScoreFromSet(key, fromScore, toScore, 1).FirstOrDefault();

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (double.IsNaN(fromScore) || double.IsNaN(toScore) || fromScore > toScore)
            throw new ArgumentOutOfRangeException(nameof(fromScore));
        return Read(s => s.Query($"""
            SELECT I.CONTENT {LiveItems} AND I.SCORE BETWEEN :p2 AND :p3 ORDER BY I.SCORE, I.ID LIMIT :p4
            """, r => SqlSession.Json<string>(r, 0), Set, Key(key), Math.Max(double.MinValue, fromScore),
            Math.Min(double.MaxValue, toScore), count));
    }

    public override bool GetSetContains(string key, string value) => Read(s => s.Count($"""
        SELECT COUNT(*) {LiveItems} AND I.ITEM_KEY = :p2
        """, Set, Key(key), Key(value)) != 0);

    public override long GetSetCount(string key) => ItemCount(Set, key);
    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (limit == 0) return 0;

        var digests = keys.Distinct(StringComparer.Ordinal).Select(Key).ToArray();
        if (digests.Length == 0) return 0;

        var predicate = SqlSession.InParameters("I.K", digests.Length);
        return Read(s => s.Count($"""
            SELECT COUNT(*) FROM (
                SELECT 1 FROM WHF_ITEM I JOIN WHF_COLLECTION C ON C.KIND = I.KIND AND C.K = I.K
                WHERE I.KIND = 'S' AND {predicate}
                  AND (C.EXPIRES_UTC IS NULL OR C.EXPIRES_UTC > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))
                LIMIT :p{digests.Length}
            ) MATCHING_ITEMS
            """, [.. digests, limit]));
    }

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt) =>
        GetRange(Set, key, "I.SCORE, I.ID", startingFrom, endingAt);

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt) =>
        GetRange(List, key, "I.ID DESC", startingFrom, endingAt);

    private List<string> GetRange(string kind, string key, string order, int from, int to)
    {
        var length = RangeLength(from, to);
        return length == 0 ? [] : Read(s => s.Query($"""
            SELECT I.CONTENT {LiveItems} ORDER BY {order} LIMIT :p2 OFFSET :p3
            """, r => SqlSession.Json<string>(r, 0), kind, Key(key), length, from));
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        using var transaction = CreateWriteTransaction();
        transaction.SetRangeInHash(key, keyValuePairs);
        transaction.Commit();
    }

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key)
    {
        var rows = Read(s => s.Query($"SELECT I.CONTENT {LiveItems}",
            r => SqlSession.Json<KeyValuePair<string, string>>(r, 0), Hash, Key(key)));
        return rows.Count == 0 ? null : rows.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    public override string? GetValueFromHash(string key, string name) => Read(s => s.Query($"""
        SELECT I.CONTENT {LiveItems} AND I.ITEM_KEY = :p2
        """, r => SqlSession.Json<KeyValuePair<string, string>>(r, 0).Value, Hash, Key(key), Key(name)).SingleOrDefault());

    public override long GetHashCount(string key) => ItemCount(Hash, key);
    public override long GetListCount(string key) => ItemCount(List, key);
    public override List<string> GetAllItemsFromList(string key) => ReadItems(List, key, "ORDER BY I.ID DESC");
    public override TimeSpan GetSetTtl(string key) => Ttl(Set, key);
    public override TimeSpan GetHashTtl(string key) => Ttl(Hash, key);
    public override TimeSpan GetListTtl(string key) => Ttl(List, key);
    public override long GetCounter(string key) => Read(s => Counter(s, key));
    public override DateTime GetUtcDateTime() => Read(s => s.UtcNow());

    private long ItemCount(string kind, string key) =>
        Read(s => s.Count($"SELECT COUNT(*) {LiveItems}", kind, Key(key)));

    private List<string> ReadItems(string kind, string key, string order) => Read(s => s.Query($"""
        SELECT I.CONTENT {LiveItems} {order}
        """, r => SqlSession.Json<string>(r, 0), kind, Key(key)));

    private TimeSpan Ttl(string kind, string key) => Read(s =>
    {
        var expiry = s.Query("SELECT EXPIRES_UTC FROM WHF_COLLECTION WHERE KIND = :p0 AND K = :p1",
            r => SqlSession.OptionalUtc(r, 0), kind, Key(key)).SingleOrDefault();
        var now = s.UtcNow();
        return expiry > now ? expiry.Value - now : TimeSpan.FromSeconds(-1);
    });

    private T Write<T>(Func<SqlSession, T> write) => storage.Database.Write(s =>
    {
        VerifyLocks();
        var result = write(s);
        VerifyLocks();
        return result;
    });

    private void Write(Action<SqlSession> write) => Write(s => { write(s); return 0; });

    private T Read<T>(Func<SqlSession, T> read)
    {
        VerifyLocks();
        return storage.Database.Read(read);
    }

    private sealed class HeldLock(DamengMutex mutex)
    {
        internal DamengMutex Mutex { get; } = mutex;
        internal int Count { get; set; }
    }

    private sealed class LockScope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed record ServerContent(string Id, int WorkerCount, string[] Queues);
