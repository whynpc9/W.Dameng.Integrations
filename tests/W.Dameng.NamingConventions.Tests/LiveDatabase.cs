using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using W.Dm;
using Xunit;

namespace W.Dameng.NamingConventions.Tests;

internal sealed class LiveDatabase
{
    private const string CurrentSchemaSql = "SELECT SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()) FROM DUAL";
    private readonly string connectionString;
    public string Schema { get; }
    public string Tablespace { get; }

    public LiveDatabase()
    {
        var path = Environment.GetEnvironmentVariable("NAMING_TEST_SETTINGS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("Run scripts/namingconventions/run.sh provision and then test.");

        try
        {
            var owned = JsonSerializer.Deserialize<OwnedDatabase>(File.ReadAllText(path));
            if (owned is null || !owned.Ready || !Regex.IsMatch(owned.Schema, @"\ANC_U_[A-F0-9]{20}\z")
                || owned.Tablespace != "NC_TS_" + owned.Schema[5..])
                throw new InvalidOperationException();

            Schema = owned.Schema;
            Tablespace = owned.Tablespace;
            var builder = new DmConnectionStringBuilder { ConnectionString = owned.ConnectionString };
            if (builder.User != Schema)
                throw new InvalidOperationException();

            builder["pooling"] = false;
            connectionString = builder.ConnectionString;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The naming conventions ownership record is invalid.");
        }
    }

    public DmConnection Connect()
    {
        var connection = new DmConnection();
        try
        {
            connection.ConnectionString = connectionString;
            connection.Open();
            Assert.Equal(Schema, Scalar(connection, CurrentSchemaSql));
            return connection;
        }
        catch (Exception)
        {
            connection.Dispose();
            throw new InvalidOperationException("Could not open WDriver on the recorded test schema.");
        }
    }

    public string Table(string name) => $"{Quote(Schema)}.{Quote(name)}";

    public string[] Catalog(DbConnection connection, string field, string view, string predicate)
    {
        using var command = Command(connection, $"SELECT {field} FROM {view} WHERE {predicate}");
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static object? Scalar(DbConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteScalar();
    }

    public static long Number(DbConnection connection, string sql)
        => Convert.ToInt64(Scalar(connection, sql), CultureInfo.InvariantCulture);

    public static void Execute(DbConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    private static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return command;
    }

    private sealed record OwnedDatabase(string Schema, string Tablespace, string ConnectionString, bool Ready);
}
