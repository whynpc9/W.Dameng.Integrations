using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.Customizer;
using TickerQ.EntityFrameworkCore.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using W.FlexLabs.Upsert.Dameng;

namespace W.TickerQ.Dameng;

public static class DamengTickerQServiceCollectionExtensions
{
    /// <summary>Registers TickerQ's native EF store with the dedicated Dameng context.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="connectionString">A W.DamengProvider connection string for the target schema.</param>
    /// <param name="configure">Optional native TickerQ options such as the scheduler node ID.</param>
    /// <param name="migrationsAssembly">The assembly containing the application's migrations, if configured.</param>
    public static IServiceCollection AddDamengTickerQ(
        this IServiceCollection services,
        string connectionString,
        Action<TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity>>? configure = null,
        string? migrationsAssembly = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddPooledDbContextFactory<DamengTickerQDbContext>(db =>
            db.UseDamengTickerQ(connectionString, migrationsAssembly));

        return services.AddTickerQ(options =>
        {
            configure?.Invoke(options);
            options.AddOperationalStore(store =>
                store.UseApplicationDbContext<DamengTickerQDbContext>(ConfigurationType.IgnoreModelCustomizer));
        });
    }

    /// <summary>Configures matching runtime and EF migration tooling options for the context.</summary>
    public static DbContextOptionsBuilder UseDamengTickerQ(
        this DbContextOptionsBuilder options,
        string connectionString,
        string? migrationsAssembly = null)
        => options.UseDamengWithWDriver(connectionString, dameng =>
        {
            if (migrationsAssembly is not null)
                dameng.MigrationsAssembly(migrationsAssembly);
        }).UseDamengUpsert();
}
