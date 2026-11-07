using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using static W.Hangfire.Dameng.DamengDatabase;

namespace W.Hangfire.Dameng;

internal static class DamengJobs
{
    internal const string Active = "(J.EXPIRES_UTC IS NULL OR J.EXPIRES_UTC > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))";

    internal static void Create(SqlSession session, long id, string payload, DateTime createdAt,
        DateTime expiresAt, KeyValuePair<string, string>[] parameters)
    {
        session.Execute("INSERT INTO WHF_JOB (ID, PAYLOAD, CREATED_UTC, EXPIRES_UTC) VALUES (:p0, :p1, :p2, :p3)",
            id, new Lob(payload), createdAt, expiresAt);
        foreach (var pair in parameters) SetParameter(session, id, pair.Key, pair.Value);
    }

    internal static string Payload(Job job) => InvocationData.SerializeJob(job).SerializePayload();

    internal static void SetParameter(SqlSession session, long id, string name, string? value)
    {
        var digest = Key(name);
        var content = Lob.Json(new KeyValuePair<string, string?>(name, value));
        if (session.Execute("UPDATE WHF_PARAM SET CONTENT = :p0 WHERE JOB_ID = :p1 AND FIELD_KEY = :p2",
                content, id, digest) == 0)
            session.Execute("INSERT INTO WHF_PARAM (JOB_ID, FIELD_KEY, CONTENT) VALUES (:p0, :p1, :p2)",
                id, digest, content);
    }

    internal static Dictionary<string, string> Parameters(SqlSession session, long id) =>
        session.Query("SELECT CONTENT FROM WHF_PARAM WHERE JOB_ID = :p0",
                r => SqlSession.Json<KeyValuePair<string, string>>(r, 0), id)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    internal static JobData Load(string payload)
    {
        var job = new JobData();
        try
        {
            job.InvocationData = InvocationData.DeserializePayload(payload);
            job.Job = job.InvocationData.DeserializeJob();
        }
        catch (JobLoadException ex)
        {
            job.LoadException = ex;
        }

        return job;
    }

    internal static void RecordState(SqlSession session, long id, string name, StateContent state, DateTime now,
        bool updateCurrent)
    {
        var stateId = session.NextId();
        session.Execute("""
            INSERT INTO WHF_STATE (ID, JOB_ID, STATE_NAME, CREATED_UTC, CONTENT) VALUES (:p0, :p1, :p2, :p3, :p4)
            """, stateId, id, name, now, Lob.Json(state));
        if (!updateCurrent) return;

        session.Execute("UPDATE WHF_JOB SET CURRENT_STATE_ID = :p0, STATE_NAME = :p1 WHERE ID = :p2", stateId, name, id);
        var metric = name switch
        {
            var value when value == SucceededState.StateName => "succeeded",
            var value when value == FailedState.StateName => "failed",
            var value when value == DeletedState.StateName => "deleted",
            _ => null
        };
        if (metric is null) return;
        DamengCollections.ChangeCounter(session, Metric(metric, now, false), 1, now.AddDays(8));
        DamengCollections.ChangeCounter(session, Metric(metric, now, true), 1, now.AddDays(2));
    }

    internal static string Metric(string metric, DateTime utc, bool hourly) =>
        FormattableString.Invariant($"w.hangfire:metrics:{metric}:{utc.ToString(hourly ? "yyyy-MM-dd-HH" : "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
}

internal sealed record StateContent(string? Reason, Dictionary<string, string> Data);
