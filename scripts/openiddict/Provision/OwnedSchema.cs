using System.Data.Common;

namespace W.OpenIddict.Dameng.Testing;

internal static class OwnedSchema
{
    internal static async Task VerifyAsync(DbConnection connection, string user, string tablespace)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT USER, SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID()), DEFAULT_TABLESPACE
            FROM SYS.USER_USERS
            """;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != user ||
            reader.GetString(1) != user || reader.GetString(2) != tablespace)
        {
            throw new InvalidOperationException("Current user, schema or tablespace does not match OpenIddict provisioning.");
        }
    }
}
