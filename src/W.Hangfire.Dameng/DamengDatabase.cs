using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire.Storage;
using W.Dm;

namespace W.Hangfire.Dameng;

internal sealed class DamengDatabase
{
    private readonly string _connectionString;
    private readonly string _lockConnectionString;

    internal DamengDatabase(string connectionString)
    {
        var settings = new DmConnectionStringBuilder(connectionString);
        if (!settings.AutoCommit)
            throw new ArgumentException("Hangfire requires driver autocommit outside explicit transactions.", nameof(connectionString));
        _connectionString = connectionString;
        settings.Pooling = false;
        _lockConnectionString = settings.ConnectionString;
    }

    internal DmConnection Open(bool dedicated = false)
    {
        var connection = new DmConnection(dedicated ? _lockConnectionString : _connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal T Read<T>(Func<SqlSession, T> read)
    {
        using var connection = Open();
        return read(new SqlSession(connection));
    }

    internal T Write<T>(Func<SqlSession, T> write)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted, false);
        var session = new SqlSession(connection, transaction);
        if (session.Scalar("SELECT K FROM WHF_LOCK WHERE K = 'WRITER' FOR UPDATE") is null)
            throw new InvalidOperationException("The Hangfire writer lock row is missing.");
        var result = write(session);
        transaction.Commit();
        return result;
    }

    internal void Write(Action<SqlSession> write) => Write(session => { write(session); return 0; });

    internal void FenceLockAcquisition(DamengMutex resourceLock, string resource, TimeSpan remaining)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted, false);
        // A waiting claimant must queue alongside writers. SKIP LOCKED would let
        // frequent writes starve new resource owners even when each write is brief.
        var seconds = (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 0, int.MaxValue);
        var wait = seconds > 0 ? FormattableString.Invariant($"WAIT {seconds}") : "NOWAIT";
        using var command = connection.CreateCommand($"SELECT K FROM WHF_LOCK WHERE K = 'WRITER' FOR UPDATE {wait}");
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(1, seconds);
        try
        {
            if (command.ExecuteScalar() is null or DBNull)
                throw new InvalidOperationException("The Hangfire writer lock row is missing.");
        }
        catch (DmException ex) when (ex is DmTimeoutException || ex.Number is -6407 or -6409)
        {
            throw new DistributedLockTimeoutException(resource);
        }
        resourceLock.Verify();
    }

    internal static string Key(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static long JobId(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture,
        out var value) && value > 0 ? value : throw new ArgumentException("Invalid job identifier.", nameof(id));

    internal static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}

internal readonly record struct Lob(string Value)
{
    internal static Lob Json<T>(T value) => new(JsonSerializer.Serialize(value));
}

internal sealed class SqlSession(DmConnection connection, DmTransaction? transaction = null)
{
    internal int Execute(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteNonQuery();
    }

    internal object? Scalar(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    internal List<T> Query<T>(string sql, Func<DmDataReader, T> project, params object?[] values)
    {
        using var command = Command(sql, values);
        using var reader = (DmDataReader)command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(project(reader));
        return result;
    }

    internal long Count(string sql, params object?[] values) =>
        Convert.ToInt64(Scalar(sql, values), CultureInfo.InvariantCulture);

    internal DateTime UtcNow() =>
        Query("SELECT SYS_EXTRACT_UTC(CURRENT_TIMESTAMP()) FROM DUAL", r => Utc(r, 0)).Single();

    internal long NextId() => Count("SELECT WHF_SEQ.NEXTVAL FROM DUAL");

    internal static DateTime Utc(DmDataReader reader, int index) =>
        DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc);

    internal static DateTime? OptionalUtc(DmDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : Utc(reader, index);

    internal static T Json<T>(DmDataReader reader, int index) => JsonSerializer.Deserialize<T>(reader.GetString(index))!;

    private DmCommand Command(string sql, object?[] values)
    {
        var command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = new DmParameter();
            parameter.ParameterName = "p" + i.ToString(CultureInfo.InvariantCulture);
            if (values[i] is Lob lob)
            {
                parameter.DmSqlType = DmDbType.Clob;
                parameter.Value = lob.Value;
            }
            else
            {
                parameter.Value = values[i] ?? DBNull.Value;
            }
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
