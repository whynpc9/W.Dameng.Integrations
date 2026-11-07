using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace W.OpenIddict.Dameng;

internal sealed class DamengTokenStore<TContext> : OpenIddictEntityFrameworkCoreTokenStore<TContext>
    where TContext : DbContext
{
    private DbSet<OpenIddictEntityFrameworkCoreToken> Rows => Context.Set<OpenIddictEntityFrameworkCoreToken>();

    public DamengTokenStore(IMemoryCache cache, TContext context, IOptionsMonitor<OpenIddictEntityFrameworkCoreOptions> options)
        : base(cache, context, options) => StoreWrites.RequireWDriver(context);

    public override ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        var date = threshold.UtcDateTime;
        var candidates = Rows.Where(token => token.CreationDate < date &&
                ((token.Status != Statuses.Inactive && token.Status != Statuses.Valid) ||
                 (token.Authorization != null && token.Authorization.Status != Statuses.Valid) ||
                 token.ExpirationDate < DateTime.UtcNow))
            .OrderBy(token => token.Id);
        return StoreWrites.PruneAsync(Context, candidates, Options.CurrentValue.DisableBulkOperations, cancellationToken);
    }

    public override ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type,
        CancellationToken cancellationToken) =>
        RevokeAsync(StoreWrites.Filter(Rows, subject, client, status, type), cancellationToken);

    public override ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return RevokeAsync(Rows.Where(token => EF.Property<string>(token, StoreWrites.ApplicationId) == identifier), cancellationToken);
    }

    public override ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return RevokeAsync(Rows.Where(token => EF.Property<string>(token, StoreWrites.AuthorizationId) == identifier), cancellationToken);
    }

    public override ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return RevokeAsync(Rows.Where(token => token.Subject == subject), cancellationToken);
    }

    private ValueTask<long> RevokeAsync(IQueryable<OpenIddictEntityFrameworkCoreToken> rows, CancellationToken cancellationToken) =>
        StoreWrites.RevokeAsync(Context, rows, Options.CurrentValue.DisableBulkOperations, cancellationToken);
}
