using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dm;
using W.OpenIddict.Dameng.Testing;

var stage = "load local state";
try
{
    var statePath = Environment.GetEnvironmentVariable("OPENIDDICT_DAMENG_TEST_STATE")
        ?? throw new InvalidOperationException();
    // Keep the lock for the full provisioning attempt, including ownership-state writes.
    using var provisionLock = new FileStream(statePath + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    if (File.Exists(statePath))
    {
        var saved = JsonSerializer.Deserialize<SchemaState>(File.ReadAllText(statePath))
            ?? throw new InvalidOperationException();
        if (saved.Status != "ready")
        {
            Console.Error.WriteLine("A partial provisioning attempt requires inspection of the local ownership record.");
            return 1;
        }

        await VerifyAsync(saved);
        return 0;
    }

    stage = "connect as provisioning administrator";
    using var source = JsonDocument.Parse(File.ReadAllText(args.Single()));
    var adminSettings = new DmConnectionStringBuilder(
        source.RootElement.GetProperty("AdminConnectionString").GetString());
    using var admin = new DmConnection(adminSettings.ConnectionString);
    admin.Open();

    stage = "plan isolated schema";
    var pageSize = Convert.ToInt32(Scalar(admin, "SELECT PAGE() FROM DUAL"), CultureInfo.InvariantCulture);
    var sizeMb = pageSize switch
    {
        4096 => 16,
        8192 => 32,
        16384 => 64,
        32768 => 128,
        _ => throw new InvalidOperationException()
    };
    var directory = Path.GetDirectoryName((string)Scalar(admin, "SELECT PATH FROM V$DATAFILE")!)
        ?? throw new InvalidOperationException();
    var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
    var user = "OID_U_" + suffix;
    var space = "OID_TS_" + suffix;
    var file = Path.Combine(directory, space + ".DBF");
    var password = "Aa7" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "#";
    var settings = new W.Dm.DmConnectionStringBuilder
    {
        ["Server"] = adminSettings["Server"],
        ["Port"] = adminSettings["Port"],
        ["User"] = user,
        ["Password"] = password,
        // The fixture supports explicitly selecting plaintext for a local test server.
        ["transport_security"] = Environment.GetEnvironmentVariable("OPENIDDICT_DAMENG_TEST_TRANSPORT") ?? "RequireTls",
        ["Pooling"] = false
    };
    var state = new SchemaState("planned", user, space, file, settings.ConnectionString);
    Save(statePath, state);

    stage = "create tablespace";
    Execute(admin, $"CREATE TABLESPACE \"{space}\" DATAFILE '{file.Replace("'", "''")}' SIZE {sizeMb}");
    Save(statePath, state with { Status = "tablespace created" });

    stage = "create schema";
    Execute(admin, $"CREATE USER \"{user}\" IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE \"{space}\"");
    Save(statePath, state with { Status = "user created" });

    stage = "grant schema privileges";
    Execute(admin, $"GRANT RESOURCE TO \"{user}\"");
    Execute(admin, $"GRANT SOI TO \"{user}\"");

    stage = "verify isolated login, current schema and default tablespace";
    await VerifyAsync(state);
    Save(statePath, state with { Status = "ready" });
    return 0;
}
catch (Exception error)
{
    // Database messages may include credentials, connection details or generated DDL.
    var code = error is System.Data.Common.DbException database ? $" ({database.ErrorCode})" : "";
    Console.Error.WriteLine($"Provisioning failed during {stage}: {error.GetType().Name}{code}.");
    return 1;
}

static async Task VerifyAsync(SchemaState state)
{
    await using var connection = new W.Dm.DmConnection(state.ConnectionString);
    await connection.OpenAsync();
    await OwnedSchema.VerifyAsync(connection, state.User, state.Tablespace);
    Console.WriteLine($"Isolated login, current schema and default tablespace verified on Dameng {connection.ServerVersion}.");
}

static object? Scalar(System.Data.Common.DbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

static void Execute(DmConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static void Save(string path, SchemaState state)
{
    var temp = path + ".tmp";
    var options = new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write
    };
    if (OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("The local provisioning script requires Unix file permissions.");
    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    using (var stream = new FileStream(temp, options))
        JsonSerializer.Serialize(stream, state);
    File.Move(temp, path, overwrite: true);
}

internal sealed record SchemaState(string Status, string User, string Tablespace, string Datafile, string ConnectionString);
