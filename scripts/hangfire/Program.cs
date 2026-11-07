using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using W.Dm;

const string source = "/Users/wanghongyi/Projects/dameng-entityframework-core/.local-test.secrets.json";
var stage = "read settings";
try
{
    var path = Environment.GetEnvironmentVariable("WHF_TEST_SETTINGS")!;
    using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var sourceSettings = JsonDocument.Parse(File.ReadAllText(source));
    var admin = new DmConnectionStringBuilder(sourceSettings.RootElement.GetProperty("AdminConnectionString").GetString())
    {
        Pooling = false,
        TransportSecurity = DmTransportSecurity.PlaintextAllowed
    };

    var existing = File.Exists(path) ? JsonSerializer.Deserialize<Allocation>(File.ReadAllText(path)) : null;
    if (existing?.Status == "ready")
    {
        stage = "verify existing allocation";
        Verify(admin.ConnectionString, existing);
        Console.WriteLine("Verified existing Hangfire test user/schema and its dedicated tablespace.");
        return 0;
    }

    stage = "open admin";
    using var connection = new DmConnection(admin.ConnectionString);
    connection.Open();
    stage = "inspect database";
    var size = Convert.ToInt32(Scalar(connection, "SELECT PAGE() FROM DUAL"), CultureInfo.InvariantCulture) / 256;
    var dataFile = Convert.ToString(Scalar(connection, "SELECT PATH FROM V$DATAFILE"), CultureInfo.InvariantCulture)!;
    var directory = Path.GetDirectoryName(dataFile);
    if (size is not (16 or 32 or 64 or 128) || string.IsNullOrEmpty(directory))
        throw new InvalidOperationException("Unsupported test server layout");
    if (existing is { Status: not ("tablespace_created" or "user_created") })
        throw new InvalidOperationException("Incomplete allocation requires inspection");
    var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
    var user = existing?.User ?? "WHF_U_" + suffix;
    var tablespace = existing?.Tablespace ?? "WHF_TS_" + suffix;
    var file = Path.Combine(directory, tablespace + ".DBF").Replace("'", "''", StringComparison.Ordinal);
    var password = "Whf1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    var test = new DmConnectionStringBuilder(admin.ConnectionString) { User = user, Password = password, Schema = "" };
    var allocation = new Allocation("planned", user, tablespace, test.ConnectionString);
    if (existing is null)
    {
        Save(path, allocation);
        stage = "create tablespace";
        Execute(connection, $"CREATE TABLESPACE {tablespace} DATAFILE '{file}' SIZE {size} AUTOEXTEND ON NEXT 16 MAXSIZE 512");
        Save(path, allocation with { Status = "tablespace_created" });
    }
    if (existing?.Status != "user_created")
    {
        allocation = allocation with { Status = "tablespace_created" };
        Save(path, allocation);
        stage = "create user";
        Execute(connection, $"CREATE USER {user} IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE {tablespace}");
        Save(path, allocation with { Status = "user_created" });
    }
    else
    {
        allocation = existing;
    }
    stage = "grant resource";
    Execute(connection, $"GRANT RESOURCE TO {user}");
    stage = "verify new allocation";
    Verify(admin.ConnectionString, allocation);
    Save(path, allocation with { Status = "ready" });
    Console.WriteLine("Created and verified a unique Hangfire test user/schema and dedicated tablespace.");
    return 0;
}
catch (Exception exception)
{
    var code = exception is DmException dm ? $" server={dm.Number}, driver={dm.DriverErrorCode}" : "";
    Console.Error.WriteLine($"Provision failed at {stage} ({exception.GetType().Name}{code}).");
    return 1;
}

static object? Scalar(DmConnection connection, string sql)
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

static void Verify(string admin, Allocation allocation)
{
    using var user = new DmConnection(allocation.ConnectionString);
    user.Open();
    if (!Equals(Scalar(user, "SELECT USER FROM DUAL"), allocation.User))
        throw new InvalidOperationException("User mismatch");
    using var administrator = new DmConnection(admin);
    administrator.Open();
    using var command = administrator.CreateCommand();
    command.CommandText = "SELECT DEFAULT_TABLESPACE FROM DBA_USERS WHERE USERNAME = :name";
    command.Parameters.Add(new DmParameter("name", allocation.User));
    if (!Equals(command.ExecuteScalar(), allocation.Tablespace))
        throw new InvalidOperationException("Tablespace mismatch");
}

static void Save(string path, Allocation allocation)
{
    if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix file permissions required");
    var pending = path + ".tmp";
    using (var file = new FileStream(pending, new FileStreamOptions
    {
        Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    }))
    {
        JsonSerializer.Serialize(file, allocation);
        file.Flush(flushToDisk: true);
    }
    File.Move(pending, path, overwrite: true);
}

internal sealed record Allocation(string Status, string User, string Tablespace, string ConnectionString);
