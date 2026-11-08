using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

    internal static ValueTask<int> SaveTrackedAsync(
        DbContext context, Func<Task<int>> stage, CancellationToken cancellationToken, bool deleteOnly = false) =>
        InTrackedTransactionAsync(context, async () =>
        {
            var count = await stage();
            if (deleteOnly)
                EnsureOnlyDeletes(context);
            if (count > 0)
                await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            return count;
        }, cancellationToken);

    private static async ValueTask<int> InTrackedTransactionAsync(
        DbContext context, Func<Task<int>> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            var count = await write();
            // Cancellation observed after the last DML must still roll back the local transaction.
            cancellationToken.ThrowIfCancellationRequested();
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

    internal static async ValueTask DeleteGraphAsync<TEntity>(
        DbContext context, Func<Task> writeDependents, TEntity root, CancellationToken cancellationToken) where TEntity : class
    {
        try
        {
            await InTrackedTransactionAsync(context, async () =>
            {
                await writeDependents();
                var entry = context.Entry(root);
                if (!await TryDeleteNullStampAsync(context, entry, cancellationToken))
                {
                    entry.State = EntityState.Deleted;
                    await SaveDeleteBatchAsync(context, 1, cancellationToken);
                }
                return 1;
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyException("An OpenIddict entity changed during deletion. Reload it before retrying.", exception);
        }
    }

    internal static async Task SaveDeleteBatchAsync(DbContext context, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOnlyDeletes(context);
        if (await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken) != count)
            throw new DbUpdateConcurrencyException("The OpenIddict delete batch did not write the expected number of entries.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static async ValueTask<bool> TryDeleteNullStampAsync<TEntity>(
        DbContext context, EntityEntry<TEntity> entry, CancellationToken cancellationToken) where TEntity : class
    {
        if (entry.Property("ConcurrencyToken").OriginalValue is not null)
            return false;

        // The published EF/WDriver stack binds an unused parameter for a tracked
        // DELETE with a NULL concurrency token. Use an explicit IS NULL predicate.
        entry.State = EntityState.Deleted;
        EnsureOnlyDeletes(context);
        var id = (string)entry.Property("Id").CurrentValue!;
        var count = await context.Set<TEntity>()
            .Where(row => EF.Property<string>(row, "Id") == id &&
                          EF.Property<string>(row, "ConcurrencyToken") == null)
            .ExecuteDeleteAsync(cancellationToken);
        if (count != 1)
            throw new DbUpdateConcurrencyException("The OpenIddict entity changed before its NULL-stamp delete.");
        cancellationToken.ThrowIfCancellationRequested();
        entry.State = EntityState.Detached;
        return true;
    }

    private static void EnsureOnlyDeletes(DbContext context)
    {
        if (context.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Modified or EntityState.Added))
            throw new InvalidOperationException("Reload the OpenIddict graph in a fresh context before deleting.");
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
