using System.Text.Json;
using System.Text.RegularExpressions;
using W.Dm;

namespace W.Hangfire.Dameng.Testing;

internal sealed record TestAllocation(string Status, string User, string Tablespace, string ConnectionString,
    string? DataFile = null)
{
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static TestAllocation Load(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix file permissions required.");
        if (File.GetUnixFileMode(path) != PrivateMode)
            throw new InvalidOperationException("The Hangfire allocation receipt must have mode 0600.");
        var allocation = JsonSerializer.Deserialize<TestAllocation>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Invalid Hangfire allocation receipt.");
        if (!Regex.IsMatch(allocation.User, @"\AWHF_U_[0-9A-F]{20}\z") ||
            allocation.Tablespace != "WHF_TS_" + allocation.User[6..] ||
            allocation.Status is not ("planned" or "tablespace_created" or "user_created" or "ready") ||
            allocation.Settings().User != allocation.User)
            throw new InvalidOperationException("Invalid Hangfire allocation identity.");
        return allocation;
    }

    internal DmConnectionStringBuilder Settings() => new(ConnectionString);

    internal void Save(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix file permissions required.");
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(pending, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateMode
            }))
            {
                JsonSerializer.Serialize(file, this);
                file.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    internal void Verify(DmConnection connection)
    {
        using var command = connection.CreateCommand("""
            SELECT USER, SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()), DEFAULT_TABLESPACE FROM USER_USERS
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != User || reader.GetString(1) != User ||
            reader.GetString(2) != Tablespace)
            throw new InvalidOperationException("Current user, schema or tablespace does not match the Hangfire allocation.");
    }
}
