using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using W.Dm;

namespace W.Dameng.Identity.Testing;

internal sealed record IdentityTestDatabase(string User, string Tablespace, string ConnectionString, bool Ready)
{
    internal static string StatePath =>
        Environment.GetEnvironmentVariable("W_DAMENG_IDENTITY_STATE")
        ?? throw new InvalidOperationException("Use scripts/identity/test.sh to set up the test environment.");

    internal static IdentityTestDatabase Load()
    {
        if (OperatingSystem.IsWindows() ||
            File.GetUnixFileMode(StatePath) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new InvalidOperationException("The isolated Identity database receipt must have mode 0600.");
        }

        var database = JsonSerializer.Deserialize<IdentityTestDatabase>(File.ReadAllText(StatePath));
        if (database is not { Ready: true } ||
            !Regex.IsMatch(database.User, @"\AIDT_U_[0-9A-F]{20}\z") ||
            database.Tablespace != "IDT_TS_" + database.User["IDT_U_".Length..] ||
            new DmConnectionStringBuilder { ConnectionString = database.ConnectionString }.User != database.User)
        {
            throw new InvalidOperationException("The Identity database receipt is incomplete or invalid.");
        }

        return database;
    }

    internal void Verify(DbConnection connection)
    {
        var schema = Scalar(connection, "SELECT SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()) FROM DUAL");
        if (!string.Equals(Convert.ToString(schema, CultureInfo.InvariantCulture), User, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The connection does not use the owned Identity schema.");
        }
    }

    internal static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return command.ExecuteScalar();
    }
}
