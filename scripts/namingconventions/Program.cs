using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dm;

const string source = "/Users/wanghongyi/Projects/dameng-entityframework-core/.local-test.secrets.json";
const string schemaSql = "SELECT SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()) FROM DUAL";
var stage = "load settings";
try
{
    var path = Environment.GetEnvironmentVariable("NAMING_TEST_SETTINGS")
        ?? throw new InvalidOperationException();
    using var original = JsonDocument.Parse(File.ReadAllText(source));
    var adminString = original.RootElement.GetProperty("AdminConnectionString").GetString();
    using var admin = new DmConnection(adminString);
    stage = "connect as administrator";
    admin.Open();

    OwnedDatabase settings;
    var created = !File.Exists(path);
    if (created)
    {
        stage = "plan isolated database";
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var schema = "NC_U_" + suffix;
        var tablespace = "NC_TS_" + suffix;
        var password = "Nc9" + Convert.ToHexString(RandomNumberGenerator.GetBytes(14));
        var existingFile = (string)Scalar(admin, "SELECT PATH FROM V$DATAFILE WHERE ROWNUM = 1")!;
        var directory = Path.GetDirectoryName(existingFile) ?? throw new InvalidOperationException();
        var file = Path.Combine(directory, tablespace + ".DBF").Replace("'", "''", StringComparison.Ordinal);
        var pageSize = Convert.ToInt64(Scalar(admin, "SELECT PAGE() FROM DUAL"), CultureInfo.InvariantCulture);
        var megabytes = pageSize switch
        {
            4096 => 16,
            8192 => 32,
            16384 => 64,
            32768 => 128,
            _ => throw new InvalidOperationException()
        };
        var endpoint = new DmConnectionStringBuilder(adminString);
        var connection = new W.Dm.DmConnectionStringBuilder
        {
            Server = endpoint.Server,
            Port = endpoint.Port,
            User = schema,
            Password = password,
            ["transport_security"] = "PlaintextAllowed"
        };
        settings = new OwnedDatabase(schema, tablespace, connection.ConnectionString, Ready: false);
        Write(path, settings);

        stage = "create dedicated tablespace";
        Execute(admin, $"CREATE TABLESPACE \"{tablespace}\" DATAFILE '{file}' SIZE {megabytes} AUTOEXTEND ON NEXT 16 MAXSIZE 256");
        stage = "create dedicated user";
        Execute(admin, $"CREATE USER \"{schema}\" IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE \"{tablespace}\"");
        stage = "grant isolated user privileges";
        Execute(admin, $"GRANT RESOURCE TO \"{schema}\"");
        Execute(admin, $"GRANT SOI TO \"{schema}\"");
    }
    else
    {
        stage = "read ready ownership record";
        settings = JsonSerializer.Deserialize<OwnedDatabase>(File.ReadAllText(path))
            ?? throw new InvalidOperationException();
        if (!settings.Ready)
            throw new InvalidOperationException();
    }

    stage = "verify owned schema and tablespace";
    if (!System.Text.RegularExpressions.Regex.IsMatch(settings.Schema, @"\ANC_U_[A-F0-9]{20}\z")
        || settings.Tablespace != "NC_TS_" + settings.Schema[5..])
        throw new InvalidOperationException();
    var space = Scalar(admin, $"SELECT DEFAULT_TABLESPACE FROM DBA_USERS WHERE USERNAME = '{settings.Schema}'");
    var count = Scalar(admin, $"SELECT COUNT(*) FROM V$TABLESPACE WHERE NAME = '{settings.Tablespace}'");
    using var test = new W.Dm.DmConnection(settings.ConnectionString);
    test.Open();
    if (!Equals(space, settings.Tablespace) || Convert.ToInt64(count, CultureInfo.InvariantCulture) != 1
        || !Equals(Scalar(test, schemaSql), settings.Schema))
        throw new InvalidOperationException();

    if (created)
        Write(path, settings with { Ready = true });
    Console.WriteLine(created
        ? "Created a unique user/schema and dedicated tablespace; verified default tablespace and WDriver current schema."
        : "Verified recorded user/schema, dedicated tablespace and WDriver current schema.");
    return 0;
}
catch (Exception error)
{
    // Driver errors may embed the original SQL or connection settings.
    var code = error switch
    {
        DmException db => $" (driver error {db.Number})",
        W.Dm.DmException db => $" (driver error {db.Number})",
        _ => ""
    };
    Console.Error.WriteLine($"Provisioning failed at {stage}: {error.GetType().Name}{code}.");
    return 1;
}

static object? Scalar(DbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    return command.ExecuteScalar();
}

static void Execute(DbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    command.ExecuteNonQuery();
}

static void Write(string path, OwnedDatabase settings)
{
    if (OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException();
    var temporary = path + "." + Guid.NewGuid().ToString("N");
    using (var stream = new FileStream(temporary, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    }))
    {
        JsonSerializer.Serialize(stream, settings);
    }
    File.Move(temporary, path, overwrite: settings.Ready);
}

internal sealed record OwnedDatabase(string Schema, string Tablespace, string ConnectionString, bool Ready);
