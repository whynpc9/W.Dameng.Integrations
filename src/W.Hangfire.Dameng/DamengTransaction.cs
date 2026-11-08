using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using static W.Hangfire.Dameng.DamengCollections;
using static W.Hangfire.Dameng.DamengDatabase;

namespace W.Hangfire.Dameng;

internal sealed class DamengTransaction(DamengStorageConnection owner) : JobStorageTransaction
{
    private readonly List<Action<SqlSession, DateTime>> _commands = [];
    private readonly HashSet<long> _jobsToFence = [];
    private DamengFetchedJob? _acknowledge;
    private bool _closed;

    public override string CreateJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var payload = DamengJobs.Payload(job);
        var snapshot = parameters.ToArray();
        var createdUtc = createdAt.ToUniversalTime();
        var expiresUtc = createdUtc + expireIn;
        var id = owner.Storage.Database.Read(s => s.NextId());
        Add((s, _) => DamengJobs.Create(s, id, payload, createdUtc, expiresUtc, snapshot));
        return Id(id);
    }

    public override void ExpireJob(string jobId, TimeSpan expireIn) =>
        Add((s, now) => s.Execute("UPDATE WHF_JOB SET EXPIRES_UTC = :p0 WHERE ID = :p1", now + expireIn, JobId(jobId)));

    public override void PersistJob(string jobId) =>
        Add((s, _) => s.Execute("UPDATE WHF_JOB SET EXPIRES_UTC = NULL WHERE ID = :p0", JobId(jobId)));

    public override void SetJobParameter(string jobId, string name, string? value) =>
        Add((s, _) => DamengJobs.SetParameter(s, JobId(jobId), name, value));

    public override void SetJobState(string jobId, IState state) => RecordState(jobId, state, true);
    public override void AddJobState(string jobId, IState state) => RecordState(jobId, state, false);

    private void RecordState(string jobId, IState state, bool updateCurrent)
    {
        var id = JobId(jobId);
        var name = state.Name;
        var content = new StateContent(state.Reason, new Dictionary<string, string>(state.SerializeData()));
        Add((s, now) => DamengJobs.RecordState(s, id, name, content, now, updateCurrent));
        if (updateCurrent) _jobsToFence.Add(id);
    }

    public override void AddToQueue(string queue, string jobId) => Add((s, _) => s.Execute("""
        INSERT INTO WHF_QUEUE (ID, JOB_ID, QUEUE_NAME) VALUES (WHF_SEQ.NEXTVAL, :p0, :p1)
        """, JobId(jobId), queue));

    public override void RemoveFromQueue(IFetchedJob fetchedJob)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (fetchedJob is not DamengFetchedJob lease || lease.Storage != owner.Storage)
            throw new ArgumentException("The queue lease belongs to another storage instance.", nameof(fetchedJob));
        if (_acknowledge != null)
            throw new InvalidOperationException("This transaction already acknowledges a queue lease.");
        _acknowledge = lease;
    }

    public override void IncrementCounter(string key) => CounterChange(key, 1, null);
    public override void IncrementCounter(string key, TimeSpan expireIn) => CounterChange(key, 1, expireIn);
    public override void DecrementCounter(string key) => CounterChange(key, -1, null);
    public override void DecrementCounter(string key, TimeSpan expireIn) => CounterChange(key, -1, expireIn);
    private void CounterChange(string key, int delta, TimeSpan? expireIn) =>
        Add((s, now) => ChangeCounter(s, key, delta, now + expireIn));

    public override void AddToSet(string key, string value) => AddToSet(key, value, 0);

    public override void AddToSet(string key, string value, double score)
    {
        if (!double.IsFinite(score)) throw new ArgumentOutOfRangeException(nameof(score));
        Insert(Set, key, value, score);
    }

    public override void AddRangeToSet(string key, IList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var digest = Key(key);
        var snapshot = items.ToArray();
        Add((s, now) =>
        {
            if (snapshot.Length == 0) return;
            Ensure(s, Set, digest, now);
            foreach (var item in snapshot) Put(s, Set, digest, item, 0, Lob.Json(item));
        });
    }

    public override void InsertToList(string key, string value) => Insert(List, key, value, 0);

    private void Insert(string kind, string key, string value, double score)
    {
        var digest = Key(key);
        Add((s, now) =>
        {
            Ensure(s, kind, digest, now);
            Put(s, kind, digest, value, score, Lob.Json(value));
        });
    }

    public override void RemoveFromSet(string key, string value) => RemoveItem(Set, key, value);
    public override void RemoveFromList(string key, string value) => RemoveItem(List, key, value);

    private void RemoveItem(string kind, string key, string value) => Add((s, _) => s.Execute("""
        DELETE FROM WHF_ITEM WHERE KIND = :p0 AND K = :p1 AND ITEM_KEY = :p2
        """, kind, Key(key), Key(value)));

    public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
    {
        var limit = RangeLength(keepStartingFrom, keepEndingAt);
        var digest = Key(key);
        Add((s, _) =>
        {
            if (limit == 0)
            {
                s.Execute("DELETE FROM WHF_ITEM WHERE KIND = 'L' AND K = :p0", digest);
                return;
            }

            s.Execute("""
                DELETE FROM WHF_ITEM WHERE KIND = 'L' AND K = :p0 AND ID NOT IN (
                    SELECT ID FROM WHF_ITEM WHERE KIND = 'L' AND K = :p1 ORDER BY ID DESC LIMIT :p2 OFFSET :p3)
                """, digest, digest, limit, keepStartingFrom);
        });
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        var snapshot = keyValuePairs.ToArray();
        Add((s, now) => SetHash(s, key, snapshot, now));
    }

    public override void RemoveHash(string key) => RemoveCollection(Hash, key);
    public override void RemoveSet(string key) => RemoveCollection(Set, key);

    private void RemoveCollection(string kind, string key) => Add((s, _) =>
        s.Execute("DELETE FROM WHF_COLLECTION WHERE KIND = :p0 AND K = :p1", kind, Key(key)));

    public override void ExpireSet(string key, TimeSpan expireIn) => Expire(Set, key, expireIn);
    public override void ExpireHash(string key, TimeSpan expireIn) => Expire(Hash, key, expireIn);
    public override void ExpireList(string key, TimeSpan expireIn) => Expire(List, key, expireIn);
    public override void PersistSet(string key) => Expire(Set, key, null);
    public override void PersistHash(string key) => Expire(Hash, key, null);
    public override void PersistList(string key) => Expire(List, key, null);

    private void Expire(string kind, string key, TimeSpan? expireIn) => Add((s, now) => s.Execute("""
        UPDATE WHF_COLLECTION SET EXPIRES_UTC = :p0 WHERE KIND = :p1 AND K = :p2
          AND (EXPIRES_UTC IS NULL OR EXPIRES_UTC > :p3)
        """, now + expireIn, kind, Key(key), now));

    public override void Commit()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _closed = true;
        owner.Storage.Database.Write(s =>
        {
            owner.VerifyLocks();
            var now = s.UtcNow();
            _acknowledge?.Verify(s, now);
            foreach (var job in _jobsToFence) owner.VerifyLease(s, job, now, _acknowledge);
            foreach (var command in _commands) command(s, now);
            _acknowledge?.Acknowledge(s);
            owner.VerifyLocks();
        });
        _acknowledge?.MarkAcknowledged();
    }

    public override void Dispose()
    {
        _closed = true;
        _commands.Clear();
        base.Dispose();
    }

    private void Add(Action<SqlSession, DateTime> command)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _commands.Add(command);
    }
}
