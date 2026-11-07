using System.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace W.OpenIddict.Dameng;

internal static class StoreWrites
{
    internal const string ApplicationId = "ApplicationId";
    internal const string AuthorizationId = "AuthorizationId";
    private const string Provider = "W.EntityFrameworkCore.Dameng.WDriver";

    internal static void RequireWDriver(DbContext context)
    {
        if (context.Database.ProviderName != Provider)
            throw new InvalidOperationException("Dameng OpenIddict stores require UseDamengWithWDriver.");
    }

    internal static async ValueTask<int> SaveTrackedAsync(
        DbContext context, Func<Task<int>> stage, CancellationToken cancellationToken, bool deleteOnly = false)
    {
        var tracker = context.ChangeTracker;
        if (!tracker.AutoDetectChangesEnabled)
            tracker.DetectChanges();
        if (tracker.HasChanges())
            throw new InvalidOperationException("Use a context with no pending changes for OpenIddict maintenance.");

        // RepeatableRead is unavailable on WDriver. Request Serializable directly and fail
        // before issuing any store DML if the driver cannot start that transaction.
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var count = await stage();
            if (deleteOnly && tracker.Entries().Any(entry => entry.State == EntityState.Modified))
                throw new InvalidOperationException("Reload the OpenIddict graph in a fresh context before deleting.");

            if (count > 0)
                await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            tracker.AcceptAllChanges();
            return count;
        }
        catch
        {
            // Failed tracked changes must not be sent by a later SaveChanges on this scope.
            foreach (var entry in tracker.Entries().Where(entry =>
                         entry.State is EntityState.Modified or EntityState.Deleted).ToArray())
                entry.State = EntityState.Detached;

            throw;
        }
    }

    internal static async ValueTask DeleteGraphAsync(
        DbContext context, Func<Task> stage, CancellationToken cancellationToken)
    {
        try
        {
            await SaveTrackedAsync(context, async () =>
            {
                await stage();
                return 1;
            }, cancellationToken, deleteOnly: true);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyException("An OpenIddict entity changed during deletion. Reload it before retrying.", exception);
        }
    }

    internal static async ValueTask<long> PruneAsync<TEntity>(
        DbContext context, IOrderedQueryable<TEntity> candidates, bool tracked, CancellationToken cancellationToken)
        where TEntity : class
    {
        const int batchSize = 1_000;
        long result = 0;
        var batch = candidates.Take(batchSize);
        for (var index = 0; index < 1_000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = tracked
                ? await SaveTrackedAsync(context, async () =>
                {
                    var entities = await batch.AsTracking().ToListAsync(cancellationToken);
                    context.RemoveRange(entities);
                    return entities.Count;
                }, cancellationToken, deleteOnly: true)
                : await batch.ExecuteDeleteAsync(cancellationToken);

            if (count == 0)
                break;

            result += count;
        }

        return result;
    }

    internal static IQueryable<TEntity> Filter<TEntity>(
        IQueryable<TEntity> query, string? subject, string? client, string? status, string? type)
        where TEntity : class
    {
        if (!string.IsNullOrEmpty(subject))
            query = query.Where(entity => EF.Property<string>(entity, "Subject") == subject);
        if (!string.IsNullOrEmpty(client))
            query = query.Where(entity => EF.Property<string>(entity, ApplicationId) == client);
        if (!string.IsNullOrEmpty(status))
            query = query.Where(entity => EF.Property<string>(entity, "Status") == status);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(entity => EF.Property<string>(entity, "Type") == type);

        return query;
    }

    internal static async ValueTask<long> RevokeAsync<TEntity>(
        DbContext context, IQueryable<TEntity> query, bool tracked, CancellationToken cancellationToken)
        where TEntity : class
    {
        // Invalidate pre-revocation snapshots so a stale EF update cannot undo the revoke.
        var concurrencyToken = Guid.NewGuid().ToString();
        if (!tracked)
            return await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => EF.Property<string>(entity, "Status"), OpenIddictConstants.Statuses.Revoked)
                .SetProperty(entity => EF.Property<string>(entity, "ConcurrencyToken"), concurrencyToken),
                cancellationToken);

        return await SaveTrackedAsync(context, async () =>
        {
            var entities = await query.AsTracking().ToListAsync(cancellationToken);
            foreach (var entity in entities)
            {
                var entry = context.Entry(entity);
                entry.Property("Status").CurrentValue = OpenIddictConstants.Statuses.Revoked;
                entry.Property("ConcurrencyToken").CurrentValue = concurrencyToken;
            }
            return entities.Count;
        }, cancellationToken);
    }
}
