using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Authorization = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization;
using Token = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken;

namespace W.OpenIddict.Dameng;

internal static class GraphDeletePages
{
    private const int PageSize = 16;

    internal static Task DeleteTokensAsync(DbContext context, IQueryable<Token> tokens, CancellationToken cancellationToken) =>
        DeleteAsync(context, tokens.OrderBy(row => row.Id).Select(row => new DeleteRow(row.Id!, row.ConcurrencyToken)),
            static row => new Token { Id = row.Id, ConcurrencyToken = row.ConcurrencyToken }, cancellationToken);

    internal static Task DeleteAuthorizationsAsync(
        DbContext context, IQueryable<Authorization> authorizations, CancellationToken cancellationToken) =>
        DeleteAsync(context, authorizations.OrderBy(row => row.Id).Select(row => new DeleteRow(row.Id!, row.ConcurrencyToken)),
            static row => new Authorization { Id = row.Id, ConcurrencyToken = row.ConcurrencyToken }, cancellationToken);

    private static async Task DeleteAsync<TEntity>(DbContext context, IQueryable<DeleteRow> candidates,
        Func<DeleteRow, TEntity> create, CancellationToken cancellationToken) where TEntity : class
    {
        var tracked = context.Set<TEntity>().Local;
        var query = candidates.Take(PageSize);
        while (true)
        {
            // The transaction sees its own deletes. Each read takes the first remaining keys
            // in database order, so no client-side collation or shifting offset is involved.
            var page = await query.ToListAsync(cancellationToken);
            if (page.Count == 0)
                return;

            var entries = new List<EntityEntry<TEntity>>(page.Count);
            foreach (var row in page)
            {
                // An existing snapshot must keep its original concurrency token. A new
                // stub uses the projected token, which EF includes in the DELETE predicate.
                var entry = tracked.FindEntry(row.Id) ?? context.Entry(create(row));
                if (!await StoreWrites.TryDeleteNullStampAsync(context, entry, cancellationToken))
                {
                    entry.State = EntityState.Deleted;
                    entries.Add(entry);
                }
            }

            if (entries.Count > 0)
                await StoreWrites.SaveDeleteBatchAsync(context, entries.Count, cancellationToken);
            // Detaching a written page prevents resending it and bounds newly tracked
            // entities. The outer transaction still owns every write and can roll it back.
            foreach (var entry in entries)
                entry.State = EntityState.Detached;
        }
    }

    private sealed record DeleteRow(string Id, string? ConcurrencyToken);
}
