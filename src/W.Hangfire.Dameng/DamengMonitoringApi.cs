using System.Globalization;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using W.Dm;
using static W.Hangfire.Dameng.DamengDatabase;

namespace W.Hangfire.Dameng;

internal sealed class DamengMonitoringApi(DamengDatabase database) : JobStorageMonitor
{
    private const string Available = "(Q.LEASE_UNTIL IS NULL OR Q.LEASE_UNTIL <= SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))";
    private const string Fetched = "Q.LEASE_UNTIL > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP())";
    private const string JobColumns = "J.ID, J.PAYLOAD, J.STATE_NAME, S.CREATED_UTC, S.CONTENT";
    private const string CurrentState = "LEFT JOIN WHF_STATE S ON S.ID = J.CURRENT_STATE_ID";

    public override IList<QueueWithTopEnqueuedJobsDto> Queues() => database.Read(s =>
    {
        var names = s.Query("SELECT DISTINCT QUEUE_NAME FROM WHF_QUEUE ORDER BY QUEUE_NAME", r => r.GetString(0));
        return names.Select(name => new QueueWithTopEnqueuedJobsDto
        {
            Name = name,
            Length = QueueCount(s, name, Available),
            Fetched = QueueCount(s, name, Fetched),
            FirstJobs = QueueJobs(s, name, Available, 0, 5, ToEnqueued)
        }).ToList();
    });

    public override IList<ServerDto> Servers() => database.Read(s => s.Query("""
        SELECT CONTENT, STARTED_UTC, HEARTBEAT_UTC FROM WHF_SERVER ORDER BY STARTED_UTC
        """, r =>
    {
        var content = SqlSession.Json<ServerContent>(r, 0);
        return new ServerDto
        {
            Name = content.Id, WorkersCount = content.WorkerCount, Queues = content.Queues,
            StartedAt = SqlSession.Utc(r, 1), Heartbeat = SqlSession.Utc(r, 2)
        };
    }));

    public override JobDetailsDto? JobDetails(string jobId)
    {
        if (!long.TryParse(jobId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;
        return database.Read(s =>
        {
            var details = s.Query($"""
                SELECT J.PAYLOAD, J.CREATED_UTC, J.EXPIRES_UTC FROM WHF_JOB J WHERE J.ID = :p0 AND {DamengJobs.Active}
                """, r =>
            {
                var job = DamengJobs.Load(r.GetString(0));
                return new JobDetailsDto
                {
                    Job = job.Job, InvocationData = job.InvocationData, LoadException = job.LoadException,
                    CreatedAt = SqlSession.Utc(r, 1), ExpireAt = SqlSession.OptionalUtc(r, 2)
                };
            }, id).SingleOrDefault();
            if (details == null) return null;

            details.Properties = DamengJobs.Parameters(s, id);
            details.History = s.Query("""
                SELECT STATE_NAME, CREATED_UTC, CONTENT FROM WHF_STATE WHERE JOB_ID = :p0 ORDER BY ID DESC
                """, r =>
            {
                var state = SqlSession.Json<StateContent>(r, 2);
                return new StateHistoryDto
                {
                    StateName = r.GetString(0), CreatedAt = SqlSession.Utc(r, 1), Reason = state.Reason, Data = state.Data
                };
            }, id);
            return details;
        });
    }

    public override StatisticsDto GetStatistics() => database.Read(s => new StatisticsDto
    {
        Enqueued = StateCount(s, EnqueuedState.StateName),
        Scheduled = StateCount(s, ScheduledState.StateName),
        Processing = StateCount(s, ProcessingState.StateName),
        Failed = StateCount(s, FailedState.StateName),
        Awaiting = StateCount(s, AwaitingState.StateName),
        Succeeded = DamengCollections.Counter(s, "stats:succeeded"),
        Deleted = DamengCollections.Counter(s, "stats:deleted"),
        Recurring = SetCount(s, "recurring-jobs"),
        Retries = SetCount(s, "retries"),
        Queues = s.Count("SELECT COUNT(DISTINCT QUEUE_NAME) FROM WHF_QUEUE"),
        Servers = s.Count("SELECT COUNT(*) FROM WHF_SERVER")
    });

    private static long SetCount(SqlSession s, string key) =>
        s.Count($"SELECT COUNT(*) {DamengCollections.LiveItems}", DamengCollections.Set, Key(key));

    public override long ScheduledCount() => CountState(ScheduledState.StateName);
    public override long ProcessingCount() => CountState(ProcessingState.StateName);
    public override long SucceededListCount() => CountState(SucceededState.StateName);
    public override long FailedCount() => CountState(FailedState.StateName);
    public override long DeletedListCount() => CountState(DeletedState.StateName);
    public override long AwaitingCount() => CountState(AwaitingState.StateName);

    private long CountState(string name) => database.Read(s => StateCount(s, name));
    private static long StateCount(SqlSession s, string name) =>
        s.Count($"SELECT COUNT(*) FROM WHF_JOB J WHERE J.STATE_NAME = :p0 AND {DamengJobs.Active}", name);

    public override long EnqueuedCount(string queue) => database.Read(s => QueueCount(s, queue, Available));
    public override long FetchedCount(string queue) => database.Read(s => QueueCount(s, queue, Fetched));
    private static long QueueCount(SqlSession s, string queue, string predicate) =>
        s.Count($"SELECT COUNT(*) FROM WHF_QUEUE Q WHERE Q.QUEUE_NAME = :p0 AND {predicate}", queue);

    public override JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage) =>
        database.Read(s => QueueJobs(s, queue, Available, from, perPage, ToEnqueued));

    public override JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage) =>
        database.Read(s => QueueJobs(s, queue, Fetched, from, perPage, (row, fetchedAt) => new FetchedJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            State = row.Job.State, FetchedAt = fetchedAt
        }));

    private static EnqueuedJobDto ToEnqueued(MonitorRow row, DateTime? fetchedAt) => new()
    {
        Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
        State = row.Job.State, EnqueuedAt = row.Job.State == EnqueuedState.StateName ? row.ChangedAt : null,
        InEnqueuedState = row.Job.State == EnqueuedState.StateName, StateData = row.Data
    };

    private static JobList<T> QueueJobs<T>(SqlSession session, string queue, string predicate, int from, int count,
        Func<MonitorRow, DateTime?, T> project)
    {
        ValidatePage(from, count);
        if (count == 0) return new JobList<T>([]);
        return new JobList<T>(session.Query($"""
            SELECT {JobColumns}, Q.FETCHED_UTC FROM WHF_QUEUE Q JOIN WHF_JOB J ON J.ID = Q.JOB_ID
              {CurrentState} WHERE Q.QUEUE_NAME = :p0 AND {predicate}
            ORDER BY Q.ID LIMIT :p1 OFFSET :p2
            """, r => Pair(ReadRow(r), row => project(row, SqlSession.OptionalUtc(r, 5))), queue, count, from));
    }

    public override JobList<ProcessingJobDto> ProcessingJobs(int from, int count) =>
        StateJobs(ProcessingState.StateName, from, count, false, row => new ProcessingJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            InProcessingState = true, StartedAt = row.ChangedAt, StateData = row.Data,
            ServerId = row.Data.GetValueOrDefault("ServerId") ?? row.Data.GetValueOrDefault("ServerName")
        });

    public override JobList<ScheduledJobDto> ScheduledJobs(int from, int count) =>
        StateJobs(ScheduledState.StateName, from, count, false, row => new ScheduledJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            InScheduledState = true, ScheduledAt = row.ChangedAt, StateData = row.Data,
            EnqueueAt = JobHelper.DeserializeNullableDateTime(row.Data.GetValueOrDefault("EnqueueAt")) ?? DateTime.MinValue
        });

    public override JobList<SucceededJobDto> SucceededJobs(int from, int count) =>
        StateJobs(SucceededState.StateName, from, count, true, row => new SucceededJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            InSucceededState = true, SucceededAt = row.ChangedAt, StateData = row.Data,
            Result = row.Data.GetValueOrDefault("Result"),
            TotalDuration = Number(row.Data, "PerformanceDuration") + Number(row.Data, "Latency")
        });

    public override JobList<FailedJobDto> FailedJobs(int from, int count) =>
        StateJobs(FailedState.StateName, from, count, true, row => new FailedJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            InFailedState = true, FailedAt = row.ChangedAt, StateData = row.Data, Reason = row.Reason,
            ExceptionType = row.Data.GetValueOrDefault("ExceptionType"),
            ExceptionMessage = row.Data.GetValueOrDefault("ExceptionMessage"),
            ExceptionDetails = row.Data.GetValueOrDefault("ExceptionDetails")
        });

    public override JobList<DeletedJobDto> DeletedJobs(int from, int count) =>
        StateJobs(DeletedState.StateName, from, count, true, row => new DeletedJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            InDeletedState = true, DeletedAt = row.ChangedAt, StateData = row.Data
        });

    public override JobList<AwaitingJobDto> AwaitingJobs(int from, int count) => database.Read(s =>
        ReadStateJobs(s, AwaitingState.StateName, from, count, false, (row, parent) => new AwaitingJobDto
        {
            Job = row.Job.Job, InvocationData = row.Job.InvocationData, LoadException = row.Job.LoadException,
            InAwaitingState = true, AwaitingAt = row.ChangedAt, StateData = row.Data, ParentStateName = parent
        }, includeParent: true));

    private JobList<T> StateJobs<T>(string state, int from, int count, bool descending, Func<MonitorRow, T> project) =>
        database.Read(s => ReadStateJobs(s, state, from, count, descending, (row, _) => project(row), false));

    private static JobList<T> ReadStateJobs<T>(SqlSession session, string state, int from, int count, bool descending,
        Func<MonitorRow, string?, T> project, bool includeParent)
    {
        ValidatePage(from, count);
        if (count == 0) return new JobList<T>([]);
        var order = descending ? "DESC" : "ASC";
        var rows = session.Query($"""
            SELECT {JobColumns} FROM WHF_JOB J {CurrentState} WHERE J.STATE_NAME = :p0 AND {DamengJobs.Active}
            ORDER BY J.ID {order} LIMIT :p1 OFFSET :p2
            """, ReadRow, state, count, from);
        return new JobList<T>(rows.Select(row => Pair(row, x => project(x, includeParent ? ParentState(session, x) : null))));
    }

    private static string? ParentState(SqlSession session, MonitorRow row)
    {
        if (!long.TryParse(row.Data.GetValueOrDefault("ParentId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;
        return session.Scalar("SELECT STATE_NAME FROM WHF_JOB WHERE ID = :p0", id) as string;
    }

    private static MonitorRow ReadRow(DmDataReader r)
    {
        var job = DamengJobs.Load(r.GetString(1));
        job.State = r.IsDBNull(2) ? null : r.GetString(2);
        var state = r.IsDBNull(4) ? new StateContent(null, []) : SqlSession.Json<StateContent>(r, 4);
        return new MonitorRow(r.GetInt64(0), job, SqlSession.OptionalUtc(r, 3), state.Reason, state.Data);
    }

    private static KeyValuePair<string, T> Pair<T>(MonitorRow row, Func<MonitorRow, T> map) => new(Id(row.Id), map(row));

    private static void ValidatePage(int from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
    }

    private static long? Number(Dictionary<string, string> values, string key) =>
        long.TryParse(values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    public override IDictionary<DateTime, long> SucceededByDatesCount() => Timeline("succeeded", false);
    public override IDictionary<DateTime, long> FailedByDatesCount() => Timeline("failed", false);
    public override IDictionary<DateTime, long> DeletedByDatesCount() => Timeline("deleted", false);
    public override IDictionary<DateTime, long> HourlySucceededJobs() => Timeline("succeeded", true);
    public override IDictionary<DateTime, long> HourlyFailedJobs() => Timeline("failed", true);
    public override IDictionary<DateTime, long> HourlyDeletedJobs() => Timeline("deleted", true);

    private Dictionary<DateTime, long> Timeline(string metric, bool hourly) => database.Read(s =>
    {
        var now = s.UtcNow();
        var start = hourly ? now.Date.AddHours(now.Hour) : now.Date;
        return Enumerable.Range(0, hourly ? 24 : 7)
            .Select(offset => hourly ? start.AddHours(-offset) : start.AddDays(-offset))
            .ToDictionary(date => date, date => DamengCollections.Counter(s, DamengJobs.Metric(metric, date, hourly)));
    });

    private sealed record MonitorRow(long Id, JobData Job, DateTime? ChangedAt, string? Reason, Dictionary<string, string> Data);
}
