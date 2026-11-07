using static W.Hangfire.Dameng.DamengDatabase;

namespace W.Hangfire.Dameng;

internal static class DamengCollections
{
    internal const string Set = "S";
    internal const string Hash = "H";
    internal const string List = "L";
    internal const string LiveItems = """
        FROM WHF_ITEM I JOIN WHF_COLLECTION C ON C.KIND = I.KIND AND C.K = I.K
        WHERE I.KIND = :p0 AND I.K = :p1
          AND (C.EXPIRES_UTC IS NULL OR C.EXPIRES_UTC > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))
        """;

    internal static void Ensure(SqlSession session, string kind, string key, DateTime now)
    {
        session.Execute("DELETE FROM WHF_COLLECTION WHERE KIND = :p0 AND K = :p1 AND EXPIRES_UTC <= :p2",
            kind, key, now);
        if (session.Count("SELECT COUNT(*) FROM WHF_COLLECTION WHERE KIND = :p0 AND K = :p1", kind, key) == 0)
            session.Execute("INSERT INTO WHF_COLLECTION (KIND, K) VALUES (:p0, :p1)", kind, key);
    }

    internal static void Put(SqlSession session, string kind, string key, string field, double score, Lob content)
    {
        var item = Key(field);
        if (kind != List && session.Execute("""
                UPDATE WHF_ITEM SET SCORE = :p0, CONTENT = :p1 WHERE KIND = :p2 AND K = :p3 AND ITEM_KEY = :p4
                """, score, content, kind, key, item) != 0)
            return;

        session.Execute("""
            INSERT INTO WHF_ITEM (ID, KIND, K, ITEM_KEY, SCORE, CONTENT) VALUES (WHF_SEQ.NEXTVAL, :p0, :p1, :p2, :p3, :p4)
            """, kind, key, item, score, content);
    }

    internal static void SetHash(SqlSession session, string key, KeyValuePair<string, string>[] values, DateTime now)
    {
        if (values.Length == 0) return;
        var digest = Key(key);
        Ensure(session, Hash, digest, now);
        foreach (var pair in values) Put(session, Hash, digest, pair.Key, 0, Lob.Json(pair));
    }

    internal static long Counter(SqlSession session, string key) => session.Count("""
        SELECT COALESCE(SUM(DELTA), 0) FROM WHF_COUNTER WHERE K = :p0
          AND (EXPIRES_UTC IS NULL OR EXPIRES_UTC > SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()))
        """, Key(key));

    internal static void ChangeCounter(SqlSession session, string key, long delta, DateTime? expiry)
    {
        var digest = Key(key);
        if (expiry is null && session.Execute("""
                UPDATE WHF_COUNTER SET DELTA = DELTA + :p0 WHERE K = :p1 AND EXPIRES_UTC IS NULL
                """, delta, digest) != 0)
            return;

        session.Execute("INSERT INTO WHF_COUNTER (ID, K, DELTA, EXPIRES_UTC) VALUES (WHF_SEQ.NEXTVAL, :p0, :p1, :p2)",
            digest, delta, expiry);
    }

    internal static long RangeLength(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        return Math.Max(0L, (long)to - from + 1);
    }
}
