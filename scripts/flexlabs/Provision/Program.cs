using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using W.Dm;

const string sourcePath = "/Users/wanghongyi/Projects/dameng-entityframework-core/.local-test.secrets.json";
const string statePath = ".local/flexlabs-isolation.json";
var step = "loading configuration";
try
{
    if (OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("FlexLabs provision requires Unix file permissions.");
    using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
    var adminString = source.RootElement.GetProperty("AdminConnectionString").GetString()!;
    using var admin = new Dm.DmConnection(adminString);
    admin.Open();
    Directory.CreateDirectory(".local");
    using var provisionLock = new FileStream(".local/flexlabs-provision.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Isolation isolation;
    if (File.Exists(statePath))
    {
        if (File.GetUnixFileMode(statePath) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new InvalidOperationException("Isolation file must have mode 0600.");
        isolation = JsonSerializer.Deserialize<Isolation>(File.ReadAllText(statePath))!;
        if (!isolation.Ready)
            throw new InvalidOperationException("Prior provisioning requires owner inspection.");
    }
    else
    {
        step = "planning schema and tablespace";
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var user = "FLEX_U_" + suffix;
        var tablespace = "FLEX_TS_" + suffix;
        var password = "Wz7_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var endpoint = new Dm.DmConnectionStringBuilder(adminString);
        var wdriver = new W.Dm.DmConnectionStringBuilder
        {
            Server = endpoint.Server,
            Port = endpoint.Port,
            User = user,
            Password = password,
            Pooling = false,
            TransportSecurity = DmTransportSecurity.PlaintextAllowed
        };
        isolation = new Isolation(user, tablespace, wdriver.ConnectionString, Ready: false);
        Save(statePath, isolation);

        var datafile = Convert.ToString(Scalar(admin, "SELECT PATH FROM V$DATAFILE"), CultureInfo.InvariantCulture)!;
        var separator = datafile.LastIndexOfAny(['/', '\\']);
        if (separator < 0)
            throw new InvalidOperationException("No server datafile directory.");
        var ownedFile = datafile[..(separator + 1)] + tablespace + ".DBF";
        var initialMb = Convert.ToInt32(Scalar(admin, "SELECT PAGE() FROM DUAL"), CultureInfo.InvariantCulture) / 256;
        step = "creating dedicated tablespace";
        Execute(admin, $"CREATE TABLESPACE \"{tablespace}\" DATAFILE '{ownedFile.Replace("'", "''")}' "
            + FormattableString.Invariant($"SIZE {initialMb} AUTOEXTEND ON NEXT 32 MAXSIZE 256"));
        step = "creating dedicated user/schema";
        Execute(admin, $"CREATE USER \"{user}\" IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE \"{tablespace}\"");
        Execute(admin, $"GRANT RESOURCE TO \"{user}\"");
        Execute(admin, $"GRANT SOI TO \"{user}\"");
    }

    step = "verifying WDriver login and tablespace ownership";
    using var client = new W.Dm.DmConnection(isolation.ConnectionString);
    client.Open();
    if (!Equals(Scalar(client, "SELECT USER FROM DUAL"), isolation.User))
        throw new InvalidOperationException("Unexpected current user.");
    var provisioned = new Dm.DmConnectionStringBuilder(adminString)
    {
        ["UID"] = isolation.User,
        ["PWD"] = new W.Dm.DmConnectionStringBuilder(isolation.ConnectionString).Password
    };
    using var schema = new Dm.DmConnection(provisioned.ConnectionString);
    schema.Open();
    if (!Equals(Scalar(schema, "SELECT DEFAULT_TABLESPACE FROM USER_USERS"), isolation.Tablespace))
        throw new InvalidOperationException("Unexpected default tablespace.");
    if (!isolation.Ready)
        Save(statePath, isolation with { Ready = true });
    Console.WriteLine("Isolated FlexLabs user/schema and default tablespace verified. WDriver login verified. State mode 0600.");
    return 0;
}
catch (Exception error)
{
    // Provider errors can contain connection metadata or DDL with credentials.
    Console.Error.WriteLine($"FlexLabs provision failed during {step} ({error.GetType().Name}, 0x{error.HResult:X8}).");
    return 1;
}

static object? Scalar(System.Data.Common.DbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

static void Execute(System.Data.Common.DbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static void Save(string path, Isolation isolation)
{
    if (OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("FlexLabs provision requires Unix file permissions.");
    var temp = path + "." + Guid.NewGuid().ToString("N");
    try
    {
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            JsonSerializer.Serialize(stream, isolation);
        }
        File.Move(temp, path, overwrite: true);
    }
    finally
    {
        File.Delete(temp);
    }
}

internal sealed record Isolation(string User, string Tablespace, string ConnectionString, bool Ready);
