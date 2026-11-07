using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore;
using W.Dm;
using Xunit;

namespace W.OpenIddict.Dameng.Tests;

public sealed class LiveDatabase : IAsyncLifetime
{
    private FileStream? runLock;
    private string connectionString = null!;

    public async Task InitializeAsync()
    {
        var path = Environment.GetEnvironmentVariable("OPENIDDICT_DAMENG_TEST_STATE")
            ?? throw new InvalidOperationException("Run scripts/openiddict/run.sh provision before live tests.");
        runLock = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using var state = JsonDocument.Parse(File.ReadAllText(path));
        var root = state.RootElement;
        Assert.Equal("ready", root.GetProperty("Status").GetString());
        var user = root.GetProperty("User").GetString()!;
        var tablespace = root.GetProperty("Tablespace").GetString()!;
        Assert.Matches(new Regex(@"^OID_U_[0-9A-F]{20}$"), user);
        Assert.Matches(new Regex(@"^OID_TS_[0-9A-F]{20}$"), tablespace);
        Assert.True(user[6..] == tablespace[7..], "Fixture user and tablespace must belong to the same allocation.");

        connectionString = root.GetProperty("ConnectionString").GetString()!;
        await using var connection = new DmConnection(connectionString);
        await connection.OpenAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT USER, DEFAULT_TABLESPACE FROM USER_USERS";
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(user == reader.GetString(0) && tablespace == reader.GetString(1),
            "Fixture connection must use its owned schema and tablespace.");

        await using var session = await OpenAsync();
        var creator = (IRelationalDatabaseCreator)session.Db.GetService<IDatabaseCreator>();
        if (!await creator.HasTablesAsync())
            await creator.CreateTablesAsync();
    }

    internal async Task<StoreSession> OpenAsync(bool tracked = false, IInterceptor? interceptor = null, bool native = false)
    {
        var services = new ServiceCollection();
        services.AddDbContext<StoreContext>(options =>
        {
            options.UseDamengWithWDriver(connectionString, dameng => dameng.CommandTimeout(20));
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
        });
        services.AddOpenIddict().AddCore(core =>
        {
            var ef = native
                ? core.DisableEntityCaching().UseEntityFrameworkCore().UseDbContext<StoreContext>()
                : core.UseDamengStores<StoreContext>();
            if (tracked)
                ef.DisableBulkOperations();
        });

        var session = new StoreSession(services.BuildServiceProvider(validateScopes: true));
        try
        {
            // Pooling is disabled. Every session uses its own physical connection.
            await session.Db.Database.OpenConnectionAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal static async Task ResetAsync(StoreContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OpenIddictTokens" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OpenIddictAuthorizations" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OpenIddictApplications" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OpenIddictScopes" """);
    }

    public Task DisposeAsync()
    {
        runLock?.Dispose();
        return Task.CompletedTask;
    }
}

internal sealed class StoreContext(DbContextOptions<StoreContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.UseOpenIddict();
}

internal sealed class StoreSession(ServiceProvider services) : IAsyncDisposable
{
    private readonly AsyncServiceScope scope = services.CreateAsyncScope();

    internal StoreContext Db => Get<StoreContext>();
    internal IOpenIddictApplicationManager Applications => Get<IOpenIddictApplicationManager>();
    internal IOpenIddictAuthorizationManager Authorizations => Get<IOpenIddictAuthorizationManager>();
    internal IOpenIddictScopeManager Scopes => Get<IOpenIddictScopeManager>();
    internal IOpenIddictTokenManager Tokens => Get<IOpenIddictTokenManager>();

    internal T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await services.DisposeAsync();
    }
}
