using System.Globalization;
using System.Text.Json;
using W.Dm;

namespace W.Hangfire.Dameng.Tests;

internal static class TestDatabase
{
    internal static DamengStorage Open() => OpenWith(new DamengStorageOptions
    {
        QueuePollInterval = TimeSpan.FromMilliseconds(250),
        InvisibilityTimeout = TimeSpan.FromSeconds(6),
        LeaseRenewalInterval = TimeSpan.FromSeconds(1),
        ExpirationCheckInterval = TimeSpan.FromSeconds(5)
    });

    internal static DamengStorage OpenWith(DamengStorageOptions options) => new(
        new DmConnectionStringBuilder(ConnectionString()) { Pooling = true }.ConnectionString, options);

    internal static string ConnectionString()
    {
        var path = Environment.GetEnvironmentVariable("WHF_TEST_SETTINGS")
            ?? throw new InvalidOperationException("Run scripts/hangfire/run.sh provision, then test.");
        using var settings = JsonDocument.Parse(File.ReadAllText(path));
        var root = settings.RootElement;
        var user = root.GetProperty("User").GetString()!;
        var tablespace = root.GetProperty("Tablespace").GetString()!;
        var connectionString = root.GetProperty("ConnectionString").GetString()!;
        var builder = new DmConnectionStringBuilder(connectionString);
        if (root.GetProperty("Status").GetString() != "ready" || !user.StartsWith("WHF_U_", StringComparison.Ordinal) ||
            !tablespace.StartsWith("WHF_TS_", StringComparison.Ordinal) ||
            user != builder.User || tablespace[7..] != user[6..])
            throw new InvalidOperationException("The settings do not identify the provisioned Hangfire test schema.");
        return connectionString;
    }

    internal static int Execute(string sql, params object[] args) => Access(sql, cmd => cmd.ExecuteNonQuery(), args);
    internal static object? Scalar(string sql, params object[] args) => Access(sql, cmd => cmd.ExecuteScalar(), args);
    internal static long Number(string sql, params object[] args) =>
        Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);

    private static T Access<T>(string sql, Func<DmCommand, T> action, object[] args)
    {
        using var connection = new DmConnection(ConnectionString());
        connection.Open();
        using var command = connection.CreateCommand(sql);
        for (var i = 0; i < args.Length; i++) command.Parameters.Add(new DmParameter("p" + i, args[i]));
        return action(command);
    }

    internal static string Queue() => "q" + Guid.NewGuid().ToString("N");
    internal static long Id(string id) => long.Parse(id, CultureInfo.InvariantCulture);
}
