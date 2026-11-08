using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace W.OpenIddict.Dameng;

internal sealed class DamengApplicationStore<TContext> : OpenIddictEntityFrameworkCoreApplicationStore<TContext>
    where TContext : DbContext
{
    public DamengApplicationStore(IMemoryCache cache, TContext context, IOptionsMonitor<OpenIddictEntityFrameworkCoreOptions> options)
        : base(cache, context, options) => StoreWrites.RequireWDriver(context);

    public override ValueTask DeleteAsync(OpenIddictEntityFrameworkCoreApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrEmpty(application.Id);

        return StoreWrites.DeleteGraphAsync(Context, async () =>
        {
            var authorizations = Context.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                .Where(authorization => EF.Property<string>(authorization, StoreWrites.ApplicationId) == application.Id);
            var tokens = Context.Set<OpenIddictEntityFrameworkCoreToken>()
                .Where(token => EF.Property<string>(token, StoreWrites.ApplicationId) == application.Id ||
                    authorizations.Any(authorization => authorization.Id ==
                        EF.Property<string>(token, StoreWrites.AuthorizationId)));

            await GraphDeletePages.DeleteTokensAsync(Context, tokens, cancellationToken);
            await GraphDeletePages.DeleteAuthorizationsAsync(Context, authorizations, cancellationToken);
        }, application, cancellationToken);
    }
}
