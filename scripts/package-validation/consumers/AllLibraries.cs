using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using W.FlexLabs.Upsert.Dameng;
using W.Hangfire.Dameng;
using W.OpenIddict.Dameng;
using W.TickerQ.Dameng;

namespace PackageValidation;

public static class AllLibraries
{
    // This member exists only in the fenced runtime; an upstream assembly cannot satisfy it.
    public static Guid ReadClaimToken(global::TickerQ.Utilities.Entities.TimeTickerEntity ticker)
        => ticker.ClaimToken;

    // Compiled only: validation never invokes these methods or opens a database.
    public static void Configure(IServiceCollection services, DbContextOptionsBuilder options, string connection)
    {
        options.UseDamengWithWDriver(connection).UseDamengUpsert();
        services.AddDamengTickerQ(connection);
        services.AddOpenIddict().AddCore(core => core.UseDamengStores<AuthContext>());
    }

    public static void ConfigureJobs(global::Hangfire.IGlobalConfiguration configuration, string connection)
        => configuration.UseDamengStorage(connection);
}

public sealed class AuthContext(DbContextOptions<AuthContext> options) : DbContext(options);
