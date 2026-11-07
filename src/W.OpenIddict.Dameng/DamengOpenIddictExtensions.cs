using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore;

namespace W.OpenIddict.Dameng;

public static class DamengOpenIddictExtensions
{
    /// <summary>
    /// Registers the OpenIddict 6.3 EF stores for the default string-key models and WDriver.
    /// Configure the context with UseDamengWithWDriver and the model with UseOpenIddict.
    /// </summary>
    public static OpenIddictEntityFrameworkCoreBuilder UseDamengStores<TContext>(this OpenIddictCoreBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.UseEntityFrameworkCore().UseDbContext<TContext>();
        builder.AddApplicationStore<DamengApplicationStore<TContext>>()
               .AddAuthorizationStore<DamengAuthorizationStore<TContext>>()
               .AddTokenStore<DamengTokenStore<TContext>>()
               // Set-based maintenance and cascades do not invalidate the upstream entity cache.
               .DisableEntityCaching();

        return options;
    }
}
