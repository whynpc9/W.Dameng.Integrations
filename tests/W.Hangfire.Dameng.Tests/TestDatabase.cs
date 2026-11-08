using System.Globalization;
using W.Hangfire.Dameng.Testing;
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

    internal static DamengStorage OpenWith(DamengStorageOptions options) =>
        OpenWith(Allocation(), options);

    internal static DamengStorage OpenWith(TestAllocation allocation, DamengStorageOptions options)
    {
        var builder = allocation.Settings();
        builder.Pooling = true;
        var settings = builder.ConnectionString;
        using (OpenVerified(allocation, settings)) { }
        return new DamengStorage(settings, options);
    }

    internal static TestAllocation Allocation()
    {
        var path = Environment.GetEnvironmentVariable("WHF_TEST_SETTINGS")
            ?? throw new InvalidOperationException("Run scripts/hangfire/run.sh provision, then test.");
        var allocation = TestAllocation.Load(path);
        if (allocation.Status != "ready")
            throw new InvalidOperationException("The Hangfire test allocation is incomplete.");
        return allocation;
    }

    internal static DmConnection OpenVerified()
    {
        var allocation = Allocation();
        return OpenVerified(allocation, allocation.ConnectionString);
    }

    private static DmConnection OpenVerified(TestAllocation allocation, string settings)
    {
        var connection = new DmConnection(settings);
        try
        {
            try { connection.Open(); }
            catch (DmException error)
            {
                // Driver exceptions can include endpoint details in nested errors.
                throw new InvalidOperationException(
                    $"Could not open the Hangfire test database ({error.GetType().Name}, code {error.Number}).");
            }

            allocation.Verify(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static int Execute(string sql, params object[] args) => Access(sql, cmd => cmd.ExecuteNonQuery(), args);
    internal static object? Scalar(string sql, params object[] args) => Access(sql, cmd => cmd.ExecuteScalar(), args);

    internal static object? Scalar(DmConnection verifiedConnection, string sql, params object[] args)
    {
        using var command = Command(verifiedConnection, sql, args);
        return command.ExecuteScalar();
    }

    internal static long Number(string sql, params object[] args) =>
        Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);

    private static T Access<T>(string sql, Func<DmCommand, T> action, object[] args)
    {
        using var connection = OpenVerified();
        using var command = Command(connection, sql, args);
        return action(command);
    }

    private static DmCommand Command(DmConnection verifiedConnection, string sql, object[] args)
    {
        var command = verifiedConnection.CreateCommand(sql);
        for (var i = 0; i < args.Length; i++) command.Parameters.Add(new DmParameter("p" + i, args[i]));
        return command;
    }

    internal static string Queue() => "q" + Guid.NewGuid().ToString("N");
    internal static long Id(string id) => long.Parse(id, CultureInfo.InvariantCulture);
}
