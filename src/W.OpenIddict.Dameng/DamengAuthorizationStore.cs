using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace W.OpenIddict.Dameng;

internal sealed class DamengAuthorizationStore<TContext> : OpenIddictEntityFrameworkCoreAuthorizationStore<TContext>
    where TContext : DbContext
{
    private DbSet<OpenIddictEntityFrameworkCoreAuthorization> Rows => Context.Set<OpenIddictEntityFrameworkCoreAuthorization>();

    public DamengAuthorizationStore(IMemoryCache cache, TContext context, IOptionsMonitor<OpenIddictEntityFrameworkCoreOptions> options)
        : base(cache, context, options) => StoreWrites.RequireWDriver(context);

    public override ValueTask DeleteAsync(OpenIddictEntityFrameworkCoreAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrEmpty(authorization.Id);

        var tokens = Context.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(token => EF.Property<string>(token, StoreWrites.AuthorizationId) == authorization.Id);

        return StoreWrites.DeleteGraphAsync(Context,
            () => GraphDeletePages.DeleteTokensAsync(Context, tokens, cancellationToken),
            authorization, cancellationToken);
    }

    public override ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        var date = threshold.UtcDateTime;
        var candidates = Rows.Where(authorization => authorization.CreationDate < date &&
                (authorization.Status != Statuses.Valid || authorization.Type == AuthorizationTypes.AdHoc) &&
                !authorization.Tokens.Any())
            .OrderBy(authorization => authorization.Id);
        return StoreWrites.PruneAsync(Context, candidates, Options.CurrentValue.DisableBulkOperations, cancellationToken);
    }

    public override ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type,
        CancellationToken cancellationToken) =>
        RevokeAsync(StoreWrites.Filter(Rows, subject, client, status, type), cancellationToken);

    public override ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return RevokeAsync(Rows.Where(authorization =>
            EF.Property<string>(authorization, StoreWrites.ApplicationId) == identifier), cancellationToken);
    }

    public override ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return RevokeAsync(Rows.Where(authorization => authorization.Subject == subject), cancellationToken);
    }

    private ValueTask<long> RevokeAsync(IQueryable<OpenIddictEntityFrameworkCoreAuthorization> rows,
        CancellationToken cancellationToken) =>
        StoreWrites.RevokeAsync(Context, rows, Options.CurrentValue.DisableBulkOperations, cancellationToken);
}
