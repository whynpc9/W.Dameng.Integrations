using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using W.Dameng.Identity.Testing;
using W.Dm;
using Xunit;

namespace W.Dameng.Identity.Tests;

public sealed class IdentityContext(DbContextOptions<IdentityContext> options)
    : IdentityDbContext<IdentityUser, IdentityRole, string>(options);

public sealed class IdentityContextFactory : IDesignTimeDbContextFactory<IdentityContext>
{
    // Scaffolding uses the same model and store options as the live services. No database is opened.
    public IdentityContext CreateDbContext(string[] args)
    {
        var services = IdentityDatabase.Configure(new ServiceCollection(), null).BuildServiceProvider();
        return services.GetRequiredService<IdentityContext>();
    }
}

public sealed class IdentityDatabase : IAsyncLifetime
{
    private ServiceProvider services = null!;
    private IdentityTestDatabase database = null!;

    internal static IServiceCollection Configure(IServiceCollection services, string? connectionString)
    {
        services.AddLogging();
        services.AddDbContext<IdentityContext>(options => options.UseDamengWithWDriver(connectionString));
        services.AddIdentityCore<IdentityUser>(options =>
            {
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version2;
                options.Stores.MaxLengthForKeys = 128;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<IdentityContext>();
        return services;
    }

    public async Task InitializeAsync()
    {
        database = IdentityTestDatabase.Load();
        await using var connection = OpenReadback();
        services = Configure(new ServiceCollection(), database.ConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using var scope = NewScope();
        await scope.ServiceProvider.GetRequiredService<IdentityContext>().Database.MigrateAsync();
    }

    internal string Tablespace => database.Tablespace;

    internal AsyncServiceScope NewScope() => services.CreateAsyncScope();

    internal DmConnection OpenReadback()
    {
        var connection = new DmConnection(database.ConnectionString);
        try
        {
            connection.Open();
            database.Verify(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }
    }
}
