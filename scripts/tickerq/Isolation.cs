using System.Text.Json;
using System.Text.RegularExpressions;
using W.Dm;

internal sealed record Isolation(string User, string Tablespace, string ConnectionString, bool Ready)
{
    internal const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static Isolation Read(string path, bool requireReady)
    {
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != PrivateMode)
            throw new InvalidOperationException("Incorrect isolation file permissions.");

        var isolation = JsonSerializer.Deserialize<Isolation>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Invalid isolation file.");
        if (!Regex.IsMatch(isolation.User, @"\ATQ_U_[0-9A-F]{20}\z")
            || isolation.Tablespace != "TQ_TS_" + isolation.User[5..]
            || (requireReady && !isolation.Ready))
            throw new InvalidOperationException("Invalid or incomplete TickerQ provisioning.");
        return isolation;
    }

    internal void Save(string path)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateMode;
        using (var file = new FileStream(temporaryPath, options))
            JsonSerializer.Serialize(file, this);
        File.Move(temporaryPath, path, overwrite: true);
    }

    internal void Verify()
    {
        using var connection = new DmConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT USER, SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()), DEFAULT_TABLESPACE
            FROM USER_USERS
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != User || reader.GetString(1) != User
            || reader.GetString(2) != Tablespace)
            throw new InvalidOperationException("Current user, schema or tablespace does not match provisioning.");
    }

    internal static string Failure(Exception exception)
    {
        var codes = new List<string>();
        for (var error = exception; error is not null; error = error.InnerException)
            codes.Add(error is DmException dm ? $"DmException({dm.Number}, {dm.ErrorKind})" : error.GetType().Name);
        return string.Join(" -> ", codes);
    }
}
