using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using W.Dm;
using W.Hangfire.Dameng.Testing;

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

    stage = "open admin";
    using var connection = new DmConnection(admin.ConnectionString);
    connection.Open();

    stage = "read or record allocation";
    var allocation = File.Exists(path) ? TestAllocation.Load(path) : Plan(connection, admin, path);
    var test = allocation.Settings();
    if (test.Server != admin.Server || test.Port != admin.Port)
        throw new InvalidOperationException("The allocation endpoint does not match the provisioning source.");

    if (allocation.Status == "ready")
    {
        stage = "verify existing allocation";
        VerifyLogin(allocation);
        Console.WriteLine("Verified existing Hangfire test user/schema and its dedicated tablespace.");
        return 0;
    }

    stage = "reconcile recorded allocation";
    if (allocation.DataFile is null ||
        Path.GetFileName(allocation.DataFile) != allocation.Tablespace + ".DBF" ||
        !Regex.IsMatch(test.Password, @"\AWhf1[0-9A-F]{24}\z"))
        throw new InvalidOperationException("The incomplete receipt lacks verifiable allocation data.");

    var hasUser = Scalar(connection, "SELECT DEFAULT_TABLESPACE FROM DBA_USERS WHERE USERNAME = :p0",
        allocation.User) is not null and not DBNull;
    var spaceExists = Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM V$TABLESPACE WHERE NAME = :p0",
        allocation.Tablespace), CultureInfo.InvariantCulture) != 0;
    if (allocation.Status == "planned")
    {
        // A matching name or file alone cannot prove a CREATE TABLESPACE with a lost receipt.
        if (spaceExists || hasUser)
            throw new InvalidOperationException("An object exists without a confirmed allocation receipt.");
        stage = "create tablespace";
        var size = Convert.ToInt32(Scalar(connection, "SELECT PAGE() FROM DUAL"), CultureInfo.InvariantCulture) / 256;
        if (size is not (16 or 32 or 64 or 128))
            throw new InvalidOperationException("Unsupported test server page size.");
        var file = allocation.DataFile.Replace("'", "''", StringComparison.Ordinal);
        Execute(connection, $"CREATE TABLESPACE {allocation.Tablespace} DATAFILE '{file}' SIZE {size} AUTOEXTEND ON NEXT 16 MAXSIZE 512");
        allocation = allocation with { Status = "tablespace_created" };
        allocation.Save(path);
    }

    stage = "verify recorded tablespace";
    if (!Equals(Scalar(connection, """
        SELECT DF.PATH FROM V$TABLESPACE TS JOIN V$DATAFILE DF ON TS.ID = DF.GROUP_ID
        WHERE TS.NAME = :p0 AND TS.FILE_NUM = 1
        """, allocation.Tablespace), allocation.DataFile))
        throw new InvalidOperationException("The recorded tablespace and datafile do not match.");

    if (!hasUser)
    {
        if (allocation.Status != "tablespace_created")
            throw new InvalidOperationException("The previously verified test user is missing.");
        stage = "create user";
        Execute(connection, $"CREATE USER {allocation.User} IDENTIFIED BY \"{test.Password}\" DEFAULT TABLESPACE {allocation.Tablespace}");
        if (Environment.GetEnvironmentVariable("WHF_TEST_INTERRUPT_AFTER_CREATE_USER") == "1")
        {
            Console.WriteLine("Injected interruption after CREATE USER and before saving its receipt.");
            return 75;
        }
    }

    // If CREATE USER previously succeeded, authenticate with the original secret
    // before granting anything. Never alter an existing account's credentials.
    stage = "verify user ownership";
    VerifyLogin(allocation);
    allocation = allocation with { Status = "user_created" };
    allocation.Save(path);
    stage = "grant resource";
    Execute(connection, $"GRANT RESOURCE TO {allocation.User}");
    stage = "verify new allocation";
    VerifyLogin(allocation);
    (allocation with { Status = "ready" }).Save(path);
    Console.WriteLine("Provisioned and verified a unique Hangfire test user/schema and dedicated tablespace.");
    return 0;
}
catch (Exception exception)
{
    var code = exception is DmException dm ? $" server={dm.Number}, driver={dm.DriverErrorCode}" : "";
    Console.Error.WriteLine($"Provision failed at {stage} ({exception.GetType().Name}{code}). Check the allocation receipt before retrying.");
    return 1;
}

static TestAllocation Plan(DmConnection connection, DmConnectionStringBuilder admin, string path)
{
    var dataFile = Convert.ToString(Scalar(connection, "SELECT PATH FROM V$DATAFILE"), CultureInfo.InvariantCulture);
    var directory = Path.GetDirectoryName(dataFile);
    if (string.IsNullOrEmpty(directory))
        throw new InvalidOperationException("Unsupported test server layout.");
    var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
    var user = "WHF_U_" + suffix;
    var tablespace = "WHF_TS_" + suffix;
    var test = new DmConnectionStringBuilder(admin.ConnectionString)
    {
        User = user, Password = "Whf1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), Schema = ""
    };
    var allocation = new TestAllocation("planned", user, tablespace, test.ConnectionString,
        Path.Combine(directory, tablespace + ".DBF"));
    allocation.Save(path);
    return allocation;
}

static object? Scalar(DmConnection connection, string sql, string? value = null)
{
    using var command = connection.CreateCommand(sql);
    if (value is not null) command.Parameters.Add(new DmParameter("p0", value));
    return command.ExecuteScalar();
}

static void Execute(DmConnection connection, string sql)
{
    using var command = connection.CreateCommand(sql);
    command.ExecuteNonQuery();
}

static void VerifyLogin(TestAllocation allocation)
{
    using var user = new DmConnection(allocation.ConnectionString);
    user.Open();
    allocation.Verify(user);
}
