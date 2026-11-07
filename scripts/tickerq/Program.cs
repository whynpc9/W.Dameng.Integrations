using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using W.Dm;

const string adminFile = "/Users/wanghongyi/Projects/dameng-entityframework-core/.local-test.secrets.json";
var phase = "initialization";
try
{
    var local = Path.Combine(args.Single(), ".local", "tickerq");
    Directory.CreateDirectory(local);
    using var gate = new FileStream(Path.Combine(local, "provision.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    var receipt = Path.Combine(local, "isolation.json");
    if (File.Exists(receipt))
    {
        phase = "existing isolation verification";
        Isolation.Read(receipt, requireReady: true).Verify();
        Console.WriteLine("TickerQ test user, schema and dedicated default tablespace verified.");
        return 0;
    }

    phase = "admin connection";
    using var source = JsonDocument.Parse(File.ReadAllText(adminFile));
    var adminOptions = new DmConnectionStringBuilder(source.RootElement.GetProperty("AdminConnectionString").GetString())
    {
        TransportSecurity = DmTransportSecurity.PlaintextAllowed,
        Pooling = false
    };
    using var admin = new DmConnection(adminOptions.ConnectionString);
    admin.Open();

    phase = "tablespace discovery";
    var pageSize = Convert.ToInt32(Scalar(admin, "SELECT PAGE() FROM DUAL"), CultureInfo.InvariantCulture);
    var sizeMb = pageSize switch
    {
        4096 => 16,
        8192 => 32,
        16384 => 64,
        32768 => 128,
        _ => throw new InvalidOperationException("Unsupported page size.")
    };
    var sourceDatafile = Convert.ToString(Scalar(admin, "SELECT PATH FROM V$DATAFILE"), CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException("Missing datafile path.");
    var separator = sourceDatafile.LastIndexOfAny(['/', '\\']);
    if (separator < 0)
        throw new InvalidOperationException("Cannot find server datafile directory.");

    var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
    var user = "TQ_U_" + suffix;
    var tablespace = "TQ_TS_" + suffix;
    var password = "Tq7" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    var datafile = (sourceDatafile[..(separator + 1)] + tablespace + ".DBF").Replace("'", "''", StringComparison.Ordinal);
    var options = new DmConnectionStringBuilder
    {
        Server = adminOptions.Server,
        Port = adminOptions.Port,
        User = user,
        Password = password,
        Pooling = false,
        TransportSecurity = DmTransportSecurity.PlaintextAllowed
    };
    var isolation = new Isolation(user, tablespace, options.ConnectionString, Ready: false);
    isolation.Save(receipt);

    phase = "tablespace creation";
    Execute(admin, FormattableString.Invariant(
        $"CREATE TABLESPACE \"{tablespace}\" DATAFILE '{datafile}' SIZE {sizeMb} AUTOEXTEND ON NEXT 16 MAXSIZE 256"));
    phase = "user creation";
    Execute(admin, $"CREATE USER \"{user}\" IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE \"{tablespace}\"");
    phase = "test grants";
    Execute(admin, $"GRANT RESOURCE TO \"{user}\"");
    Execute(admin, $"GRANT SOI TO \"{user}\"");

    phase = "new isolation verification";
    isolation.Verify();
    (isolation with { Ready = true }).Save(receipt);
    Console.WriteLine("Created and verified a TickerQ-only user, schema and dedicated default tablespace.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"TickerQ provisioning failed during {phase}: {Isolation.Failure(error)}");
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
