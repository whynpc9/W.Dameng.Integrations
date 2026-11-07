using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using W.Dameng.Identity.Testing;
using W.Dm;

const string sourcePath =
    "/Users/wanghongyi/Projects/dameng-entityframework-core/.local-test.secrets.json";

var phase = "reading the local receipt";
try
{
    var path = IdentityTestDatabase.StatePath;
    if (File.Exists(path))
    {
        var existing = IdentityTestDatabase.Load();
        phase = "verifying the owned schema";
        using var connection = new DmConnection(existing.ConnectionString);
        connection.Open();
        existing.Verify(connection);
        Console.WriteLine("Existing Identity isolation verified.");
        return 0;
    }

    phase = "opening the admin connection";
    using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
    var adminSettings = new DmConnectionStringBuilder
    {
        ConnectionString = source.RootElement.GetProperty("AdminConnectionString").GetString(),
        TransportSecurity = DmTransportSecurity.PlaintextAllowed,
        Pooling = false
    };
    using var admin = new DmConnection(adminSettings.ConnectionString);
    admin.Open();

    phase = "reading database storage metadata";
    var pageSize = Convert.ToInt32(IdentityTestDatabase.Scalar(admin, "SELECT PAGE() FROM DUAL"),
        CultureInfo.InvariantCulture);
    var sizeMb = pageSize switch
    {
        4096 => 16,
        8192 => 32,
        16384 => 64,
        32768 => 128,
        _ => throw new InvalidOperationException("Unsupported Dameng page size.")
    };
    var existingDatafile = Convert.ToString(
        IdentityTestDatabase.Scalar(admin, "SELECT PATH FROM V$DATAFILE"), CultureInfo.InvariantCulture);
    var directory = Path.GetDirectoryName(existingDatafile?.Replace('\\', '/'));
    if (string.IsNullOrWhiteSpace(directory))
    {
        throw new InvalidOperationException("Cannot locate the Dameng datafile directory.");
    }

    var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
    var user = "IDT_U_" + suffix;
    var tablespace = "IDT_TS_" + suffix;
    var datafile = Path.Combine(directory, tablespace + ".DBF").Replace("'", "''", StringComparison.Ordinal);
    var password = "Id9" + Convert.ToHexString(RandomNumberGenerator.GetBytes(14));
    var testSettings = new DmConnectionStringBuilder
    {
        Server = adminSettings.Server,
        Port = adminSettings.Port,
        User = user,
        Password = password,
        TransportSecurity = DmTransportSecurity.PlaintextAllowed,
        Pooling = false
    };
    var receipt = new IdentityTestDatabase(user, tablespace, testSettings.ConnectionString, Ready: false);

    phase = "reserving the local ownership receipt";
    Save(path, receipt);

    phase = "creating the dedicated tablespace";
    Execute(admin, FormattableString.Invariant(
        $"CREATE TABLESPACE \"{tablespace}\" DATAFILE '{datafile}' SIZE {sizeMb} AUTOEXTEND ON NEXT 8 MAXSIZE 256"));
    phase = "creating the dedicated user and default schema";
    Execute(admin, $"CREATE USER \"{user}\" IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE \"{tablespace}\"");
    phase = "granting the test user DDL privileges";
    Execute(admin, $"GRANT RESOURCE TO \"{user}\"");
    Execute(admin, $"GRANT SOI TO \"{user}\"");

    phase = "validating the isolated connection";
    using var test = new DmConnection(receipt.ConnectionString);
    test.Open();
    receipt.Verify(test);

    phase = "finalizing the local receipt";
    Save(path, receipt with { Ready = true });
    Console.WriteLine($"Created and verified a dedicated Identity user, schema and tablespace (page size {pageSize} bytes).");
    return 0;
}
catch (Exception error)
{
    // DDL errors can contain SQL literals. Keep connection information, paths and credentials local.
    Console.Error.WriteLine($"Identity provisioning failed while {phase} ({error.GetType().Name}).");
    return 1;
}

static void Execute(DmConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    command.ExecuteNonQuery();
}

static void Save(string path, IdentityTestDatabase receipt)
{
    var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
    if (OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The local receipt requires Unix permissions.");
    }

    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    var target = receipt.Ready ? path + ".ready" : path;
    using (var stream = new FileStream(target, options))
    {
        JsonSerializer.Serialize(stream, receipt);
        stream.Flush(flushToDisk: true);
    }

    if (receipt.Ready)
    {
        File.Move(target, path, overwrite: true);
    }
}
