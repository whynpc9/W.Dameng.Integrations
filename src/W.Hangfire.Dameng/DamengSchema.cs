using W.Dm;

namespace W.Hangfire.Dameng;

internal static class DamengSchema
{
    internal const int Version = 2;

    private static readonly string[] Baseline =
    [
        "CREATE SEQUENCE WHF_SEQ START WITH 1 INCREMENT BY 1",
        "CREATE TABLE WHF_SCHEMA (ID INT PRIMARY KEY CHECK (ID = 1), VERSION_NO INT NOT NULL)",
        """
        CREATE TABLE WHF_JOB (
          ID BIGINT PRIMARY KEY, PAYLOAD CLOB NOT NULL, CREATED_UTC TIMESTAMP(6) NOT NULL,
          EXPIRES_UTC TIMESTAMP(6), CURRENT_STATE_ID BIGINT, STATE_NAME VARCHAR(128)
        )
        """,
        """
        CREATE TABLE WHF_STATE (
          ID BIGINT PRIMARY KEY, JOB_ID BIGINT NOT NULL REFERENCES WHF_JOB(ID) ON DELETE CASCADE,
          STATE_NAME VARCHAR(128) NOT NULL, CREATED_UTC TIMESTAMP(6) NOT NULL, CONTENT CLOB NOT NULL
        )
        """,
        """
        CREATE TABLE WHF_PARAM (
          JOB_ID BIGINT NOT NULL REFERENCES WHF_JOB(ID) ON DELETE CASCADE,
          FIELD_KEY CHAR(64) NOT NULL, CONTENT CLOB NOT NULL, PRIMARY KEY (JOB_ID, FIELD_KEY)
        )
        """,
        """
        CREATE TABLE WHF_QUEUE (
          ID BIGINT PRIMARY KEY, JOB_ID BIGINT NOT NULL REFERENCES WHF_JOB(ID) ON DELETE CASCADE,
          QUEUE_NAME VARCHAR(256) NOT NULL, LEASE_TOKEN CHAR(32), FETCHED_UTC TIMESTAMP(6), LEASE_UNTIL TIMESTAMP(6)
        )
        """,
        """
        CREATE TABLE WHF_SERVER (
          SERVER_KEY CHAR(64) PRIMARY KEY, CONTENT CLOB NOT NULL,
          STARTED_UTC TIMESTAMP(6) NOT NULL, HEARTBEAT_UTC TIMESTAMP(6) NOT NULL
        )
        """,
        """
        CREATE TABLE WHF_COLLECTION (
          KIND CHAR(1) NOT NULL, K CHAR(64) NOT NULL, EXPIRES_UTC TIMESTAMP(6), PRIMARY KEY (KIND, K)
        )
        """,
        """
        CREATE TABLE WHF_ITEM (
          ID BIGINT PRIMARY KEY, KIND CHAR(1) NOT NULL, K CHAR(64) NOT NULL, ITEM_KEY CHAR(64) NOT NULL,
          SCORE DOUBLE NOT NULL, CONTENT CLOB NOT NULL,
          FOREIGN KEY (KIND, K) REFERENCES WHF_COLLECTION(KIND, K) ON DELETE CASCADE
        )
        """,
        """
        CREATE TABLE WHF_COUNTER (
          ID BIGINT PRIMARY KEY, K CHAR(64) NOT NULL, DELTA BIGINT NOT NULL, EXPIRES_UTC TIMESTAMP(6)
        )
        """
    ];

    private static readonly string[] Indexes =
    [
        "CREATE INDEX WHF_JOB_STATE_I ON WHF_JOB(STATE_NAME, ID)",
        "CREATE INDEX WHF_JOB_EXP_I ON WHF_JOB(EXPIRES_UTC, ID)",
        "CREATE INDEX WHF_STATE_JOB_I ON WHF_STATE(JOB_ID, ID)",
        "CREATE INDEX WHF_QUEUE_NAME_I ON WHF_QUEUE(QUEUE_NAME, ID)",
        "CREATE INDEX WHF_QUEUE_JOB_I ON WHF_QUEUE(JOB_ID)",
        "CREATE INDEX WHF_ITEM_KEY_I ON WHF_ITEM(KIND, K, ITEM_KEY)",
        "CREATE INDEX WHF_ITEM_SCORE_I ON WHF_ITEM(KIND, K, SCORE, ID)",
        "CREATE INDEX WHF_COUNTER_KEY_I ON WHF_COUNTER(K, EXPIRES_UTC)",
        "CREATE INDEX WHF_COUNTER_EXP_I ON WHF_COUNTER(EXPIRES_UTC, ID)",
        "CREATE INDEX WHF_COLLECTION_EXP_I ON WHF_COLLECTION(EXPIRES_UTC)"
    ];

    internal static void Ensure(DamengDatabase database, TimeSpan timeout)
    {
        if (ReadVersion(database) == Version) return;

        if (!database.Read(s => Exists(s, "USER_TABLES", "TABLE_NAME", "WHF_LOCK")))
        {
            try
            {
                database.Read(s => s.Execute("CREATE TABLE IF NOT EXISTS WHF_LOCK (K VARCHAR(64) PRIMARY KEY)"));
            }
            catch (DmException)
            {
                // Concurrent DDL can still fail at the catalog insert. Continue only
                // when a fresh session confirms that another installer created the table.
                if (!database.Read(s => Exists(s, "USER_TABLES", "TABLE_NAME", "WHF_LOCK"))) throw;
            }
        }

        using var lease = DamengMutex.Acquire(database, "SCHEMA", "schema initialization", timeout);
        var version = ReadVersion(database);
        if (version is null)
        {
            foreach (var sql in Baseline) CreateMissing(database, sql);
            lease.Verify();
            database.Read(s =>
            {
                DamengMutex.EnsureRow(s, "WRITER");
                return s.Execute("INSERT INTO WHF_SCHEMA (ID, VERSION_NO) VALUES (1, 1)");
            });
            version = 1;
        }

        if (version is < 1 or > Version)
            throw new InvalidOperationException($"Unsupported Hangfire schema version {version}.");
        if (version == 1)
        {
            // DDL commits independently. Replay unfinished steps after interruption.
            foreach (var sql in Indexes) CreateMissing(database, sql);
            lease.Verify();
            database.Read(s => s.Execute("UPDATE WHF_SCHEMA SET VERSION_NO = 2 WHERE ID = 1"));
        }
    }

    private static long? ReadVersion(DamengDatabase database) => database.Read<long?>(s =>
    {
        if (!Exists(s, "USER_TABLES", "TABLE_NAME", "WHF_SCHEMA")) return null;
        var version = s.Scalar("SELECT VERSION_NO FROM WHF_SCHEMA WHERE ID = 1");
        return version is null ? null : Convert.ToInt64(version, System.Globalization.CultureInfo.InvariantCulture);
    });

    private static bool Exists(SqlSession session, string catalog, string column, string name) =>
        session.Count($"SELECT COUNT(*) FROM {catalog} WHERE {column} = :p0", name) != 0;

    private static void CreateMissing(DamengDatabase database, string ddl)
    {
        var parts = ddl.Split([' ', '\n', '('], StringSplitOptions.RemoveEmptyEntries);
        var (catalog, column) = parts[1] switch
        {
            "TABLE" => ("USER_TABLES", "TABLE_NAME"),
            "INDEX" => ("USER_INDEXES", "INDEX_NAME"),
            "SEQUENCE" => ("USER_SEQUENCES", "SEQUENCE_NAME"),
            _ => throw new InvalidOperationException("Unsupported Hangfire DDL.")
        };

        database.Read(s => Exists(s, catalog, column, parts[2]) ? 0 : s.Execute(ddl));
    }
}
