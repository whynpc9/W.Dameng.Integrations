using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Abstractions.OpenIddictExceptions;
using Application = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication;
using Authorization = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization;
using Token = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken;

namespace W.OpenIddict.Dameng.Tests;

public sealed partial class StoreTests
{
    private const int GraphTokenCount = 35;
    private const int DeletePageSize = 16;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GraphDeletionReadsOnlyKeysAndStampsAcrossPages(bool deleteApplication)
    {
        await using var seed = await live.OpenAsync();
        var graph = await SeedGraphAsync(seed, deleteApplication, large: true);
        await using (var observer = await live.OpenAsync())
        {
            Assert.Equal(GraphTokenCount, await observer.Db.Set<Token>()
                .CountAsync(row => row.Id != graph.OutsideToken.Id && row.Payload != null && row.Properties != null));
            var sample = (await observer.Tokens.FindByIdAsync(graph.Sample.Id!))!;
            Assert.Equal(graph.Sample.Payload, await observer.Tokens.GetPayloadAsync(sample));
        }

        if (deleteApplication)
        {
            var fault = new GraphSaveProbe(afterSave: probe =>
            {
                if (probe.TokenPages == 2)
                    throw new RejectedSave();
            });
            await using (var writer = await live.OpenAsync(interceptor: fault))
            {
                var root = await FindRootAsync(writer, graph, deleteApplication);
                await Assert.ThrowsAsync<RejectedSave>(() => DeleteRootAsync(writer, root, deleteApplication));
                Assert.Equal(2 * DeletePageSize, fault.CompletedTokenRows);
                Assert.False(writer.Db.ChangeTracker.HasChanges());
                Assert.Equal(0, await writer.Db.SaveChangesAsync());
            }

            await using var observer = await live.OpenAsync();
            Assert.Equal(2, await observer.Applications.CountAsync());
            Assert.Equal(18, await observer.Authorizations.CountAsync());
            Assert.Equal(GraphTokenCount + 1, await observer.Tokens.CountAsync());
            var sample = (await observer.Tokens.FindByIdAsync(graph.Sample.Id!))!;
            Assert.Equal(graph.Sample.Payload, await observer.Tokens.GetPayloadAsync(sample));
            Assert.Equal(graph.Sample.Properties, ((Token)sample).Properties);
        }

        var saves = new GraphSaveProbe();
        var reads = new GraphReadProbe();
        await using (var writer = await live.OpenAsync(interceptor: saves, commandProbe: reads))
        {
            var root = await FindRootAsync(writer, graph, deleteApplication);
            reads.Enabled = true;
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            await DeleteRootAsync(writer, root, deleteApplication);
            timer.Stop();
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            reads.Enabled = false;
            Assert.Empty(writer.Db.ChangeTracker.Entries<Token>());
            Assert.Empty(writer.Db.ChangeTracker.Entries<Authorization>());
            output.WriteLine(
                "root={0} token_rows={1} token_pages={2} select_commands={3} peak_tracked_dependents={4} " +
                "new_token_lob_UTF16_bytes={5} process_managed_allocated_bytes={6} delete_ms={7}",
                deleteApplication ? "application" : "authorization", saves.CompletedTokenRows, saves.TokenPages,
                reads.Projections.Count, saves.PeakTrackedDependents, saves.LobBytes, allocated, timer.ElapsedMilliseconds);
        }

        Assert.Equal(GraphTokenCount, saves.CompletedTokenRows);
        Assert.Equal(3, saves.TokenPages);
        Assert.Equal(deleteApplication ? 6 : 4, saves.CompletedBatches);
        Assert.Equal(DeletePageSize, saves.PeakTrackedDependents);
        Assert.Equal(0, saves.LobBytes);
        Assert.Equal(deleteApplication ? 7 : 4, reads.Projections.Count);
        Assert.All(reads.Projections, columns => Assert.Equal(new[] { "Id", "ConcurrencyToken" }, columns));
        await AssertRemainingGraphAsync(graph, deleteApplication);
    }

    [Theory]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 3, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 3, false)]
    [InlineData(true, 2, true)]
    public async Task GraphDeletionRollsBackEarlierPagesOnStaleSnapshot(bool deleteApplication, int page, bool staleAuthorization)
    {
        await using var seed = await live.OpenAsync();
        var graph = await SeedGraphAsync(seed, deleteApplication);
        var saves = new GraphSaveProbe();

        await using (var writer = await live.OpenAsync(interceptor: saves))
        {
            var root = await FindRootAsync(writer, graph, deleteApplication);
            if (staleAuthorization)
            {
                const string id = "probe-auth-16";
                Assert.NotNull(await writer.Authorizations.FindByIdAsync(id));
                var current = Assert.IsType<Authorization>(await seed.Authorizations.FindByIdAsync(id));
                current.Status = Statuses.Revoked;
                await seed.Authorizations.UpdateAsync(current);
            }
            else
            {
                var id = $"probe-{(page - 1) * DeletePageSize + 1:D2}";
                Assert.NotNull(await writer.Tokens.FindByIdAsync(id));
                var current = Assert.IsType<Token>(await seed.Tokens.FindByIdAsync(id));
                current.Payload = "updated from another connection";
                await seed.Tokens.UpdateAsync(current);
            }

            var before = await ReadSnapshotAsync();
            await Assert.ThrowsAsync<ConcurrencyException>(() => DeleteRootAsync(writer, root, deleteApplication));
            Assert.Equal(staleAuthorization ? GraphTokenCount : (page - 1) * DeletePageSize, saves.CompletedTokenRows);
            Assert.Equal(staleAuthorization ? 4 : page - 1, saves.CompletedBatches);
            await AssertCleanAndUnchangedAsync(writer, before);
        }

        await RetryGraphAsync(graph, deleteApplication);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GraphDeletionRejectsStaleRootAfterWritingAllDependentPages(bool deleteApplication)
    {
        await using var seed = await live.OpenAsync();
        var graph = await SeedGraphAsync(seed, deleteApplication);
        var saves = new GraphSaveProbe();

        await using (var writer = await live.OpenAsync(interceptor: saves))
        {
            var root = await FindRootAsync(writer, graph, deleteApplication);
            if (deleteApplication)
            {
                graph.App.DisplayName = "updated root";
                await seed.Applications.UpdateAsync(graph.App);
            }
            else
            {
                graph.Authorization.Status = Statuses.Revoked;
                await seed.Authorizations.UpdateAsync(graph.Authorization);
            }

            var before = await ReadSnapshotAsync();
            await Assert.ThrowsAsync<ConcurrencyException>(() => DeleteRootAsync(writer, root, deleteApplication));
            Assert.Equal(GraphTokenCount, saves.CompletedTokenRows);
            Assert.Equal(deleteApplication ? 5 : 3, saves.CompletedBatches);
            await AssertCleanAndUnchangedAsync(writer, before);
        }

        await RetryGraphAsync(graph, deleteApplication);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GraphDeletionRollsBackAfterWritingTwoPages(bool cancel)
    {
        var deleteApplication = !cancel;
        await using var seed = await live.OpenAsync();
        var graph = await SeedGraphAsync(seed, deleteApplication);
        var before = await ReadSnapshotAsync();

        using var cancellation = new CancellationTokenSource();
        var saves = new GraphSaveProbe(afterSave: probe =>
        {
            if (probe.TokenPages == 2)
            {
                if (!cancel)
                    throw new RejectedSave();
                cancellation.Cancel();
            }
        });

        await using (var writer = await live.OpenAsync(interceptor: saves))
        {
            var root = await FindRootAsync(writer, graph, deleteApplication);
            if (cancel)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => DeleteRootAsync(writer, root, deleteApplication, cancellation.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<RejectedSave>(() => DeleteRootAsync(writer, root, deleteApplication));
            }
            Assert.Equal(2 * DeletePageSize, saves.CompletedTokenRows);
            Assert.Equal(2, saves.CompletedBatches);
            await AssertCleanAndUnchangedAsync(writer, before);
        }

        await RetryGraphAsync(graph, deleteApplication, preloadTokens: true);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task GraphDeletionUsesNullOriginalStampAndDetectsItsChange(bool deleteApplication, bool changeRoot)
    {
        await using var seed = await live.OpenAsync();
        await live.ResetAsync(seed.Db);
        var app = await CreateApplication(seed, "null-stamp");
        var auth = await CreateAuthorization(seed, app.Id);
        var changed = await CreateToken(seed, app.Id, auth.Id);
        var staysNull = await CreateToken(seed, app.Id, auth.Id);
        await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OpenIddictTokens" SET "ConcurrencyToken" = NULL
            WHERE "Id" = {changed.Id} OR "Id" = {staysNull.Id}
            """);
        await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OpenIddictAuthorizations" SET "ConcurrencyToken" = NULL WHERE "Id" = {auth.Id}
            """);
        await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OpenIddictApplications" SET "ConcurrencyToken" = NULL WHERE "Id" = {app.Id}
            """);

        await using (var writer = await live.OpenAsync())
        {
            var old = Assert.IsType<Token>(await writer.Tokens.FindByIdAsync(changed.Id!));
            Assert.Null(old.ConcurrencyToken);
            var root = deleteApplication
                ? (await writer.Applications.FindByIdAsync(app.Id!))!
                : (await writer.Authorizations.FindByIdAsync(auth.Id!))!;
            Assert.Null(deleteApplication ? ((Application)root).ConcurrencyToken : ((Authorization)root).ConcurrencyToken);

            var stamp = Guid.NewGuid().ToString();
            FormattableString update = (changeRoot, deleteApplication) switch
            {
                (true, true) => $"""UPDATE "OpenIddictApplications" SET "ConcurrencyToken" = {stamp} WHERE "Id" = {app.Id}""",
                (true, false) => $"""UPDATE "OpenIddictAuthorizations" SET "ConcurrencyToken" = {stamp} WHERE "Id" = {auth.Id}""",
                _ => $"""UPDATE "OpenIddictTokens" SET "ConcurrencyToken" = {stamp} WHERE "Id" = {changed.Id}"""
            };
            Assert.Equal(1, await seed.Db.Database.ExecuteSqlInterpolatedAsync(update));
            var before = await ReadSnapshotAsync();
            await Assert.ThrowsAsync<ConcurrencyException>(() => DeleteRootAsync(writer, root, deleteApplication));
            await AssertCleanAndUnchangedAsync(writer, before);
        }

        await using (var retry = await live.OpenAsync())
        {
            var root = deleteApplication
                ? (await retry.Applications.FindByIdAsync(app.Id!))!
                : (await retry.Authorizations.FindByIdAsync(auth.Id!))!;
            await DeleteRootAsync(retry, root, deleteApplication);
            Assert.Equal(0, await retry.Db.SaveChangesAsync());
        }

        await using var observer = await live.OpenAsync();
        Assert.Equal(deleteApplication ? 0 : 1, await observer.Applications.CountAsync());
        Assert.Equal(0, await observer.Authorizations.CountAsync());
        Assert.Equal(0, await observer.Tokens.CountAsync());
    }

    private async Task<GraphFixture> SeedGraphAsync(StoreSession seed, bool deleteApplication, bool large = false)
    {
        await live.ResetAsync(seed.Db);
        var app = await CreateApplication(seed, "graph-probe");
        var otherApp = await CreateApplication(seed, "outside-app");
        var authorization = await CreateAuthorization(seed, app.Id);
        var otherAuth = await CreateAuthorization(seed, otherApp.Id);
        var outside = await CreateToken(seed, deleteApplication ? otherApp.Id : app.Id, otherAuth.Id);

        var descriptor = new OpenIddictTokenDescriptor
        {
            ApplicationId = app.Id, AuthorizationId = authorization.Id, Subject = Subject,
            Status = Statuses.Valid, Type = TokenTypeHints.AccessToken,
            Payload = large ? new string('p', 32 * 1024) : "small payload",
            Properties = { ["probe"] = JsonSerializer.SerializeToElement(large ? new string('j', 16 * 1024) : "small property") }
        };
        var sample = (Token)await seed.Tokens.CreateAsync(descriptor);

        var copied = await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OpenIddictTokens" ("Id", "ApplicationId", "AuthorizationId", "ConcurrencyToken", "Payload", "Properties", "Status", "Type", "Subject")
            SELECT 'probe-' || LPAD(copies."N", 2, '0'),
                   CASE MOD(copies."N", 3) WHEN 0 THEN NULL WHEN 1 THEN {otherApp.Id} ELSE t."ApplicationId" END,
                   CASE WHEN MOD(copies."N", 3) = 2 AND {(deleteApplication ? 1 : 0)} = 1
                        THEN {otherAuth.Id} ELSE t."AuthorizationId" END,
                   t."ConcurrencyToken", t."Payload", t."Properties", t."Status", t."Type", t."Subject"
            FROM "OpenIddictTokens" t
            CROSS JOIN (SELECT LEVEL AS "N" FROM DUAL CONNECT BY LEVEL <= {GraphTokenCount - 1}) copies
            WHERE t."Id" = {sample.Id}
            """);
        Assert.Equal(GraphTokenCount - 1, copied);
        if (deleteApplication)
        {
            Assert.Equal(16, await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "OpenIddictAuthorizations" ("Id", "ApplicationId", "ConcurrencyToken", "Status", "Type", "Subject")
                SELECT 'probe-auth-' || LPAD(LEVEL, 2, '0'), {app.Id}, {authorization.ConcurrencyToken},
                       {Statuses.Valid}, {AuthorizationTypes.Permanent}, {Subject}
                FROM DUAL CONNECT BY LEVEL <= 16
                """));
        }
        return new(app, authorization, outside, sample);
    }

    private static async Task<object> FindRootAsync(StoreSession session, GraphFixture graph, bool deleteApplication) =>
        (deleteApplication
            ? await session.Applications.FindByIdAsync(graph.App.Id!)
            : await session.Authorizations.FindByIdAsync(graph.Authorization.Id!))
        ?? throw new InvalidOperationException("Expected a persisted graph root.");

    private static Task DeleteRootAsync(StoreSession session, object root, bool deleteApplication,
        CancellationToken cancellationToken = default) =>
        deleteApplication ? session.Applications.DeleteAsync(root, cancellationToken).AsTask()
                          : session.Authorizations.DeleteAsync(root, cancellationToken).AsTask();

    private async Task RetryGraphAsync(GraphFixture graph, bool deleteApplication, bool preloadTokens = false)
    {
        await using (var retry = await live.OpenAsync())
        {
            Token? outside = null;
            if (preloadTokens)
            {
                Assert.NotNull(await retry.Tokens.FindByIdAsync("probe-01"));
                Assert.NotNull(await retry.Tokens.FindByIdAsync("probe-33"));
                outside = Assert.IsType<Token>(await retry.Tokens.FindByIdAsync(graph.OutsideToken.Id!));
            }

            await DeleteRootAsync(retry, await FindRootAsync(retry, graph, deleteApplication), deleteApplication);
            if (outside is not null)
            {
                Assert.Same(outside, Assert.Single(retry.Db.ChangeTracker.Entries<Token>()).Entity);
                Assert.Equal(EntityState.Unchanged, retry.Db.Entry(outside).State);
                Assert.Equal(0, await retry.Db.SaveChangesAsync());
            }
        }
        await AssertRemainingGraphAsync(graph, deleteApplication);
    }

    private async Task AssertRemainingGraphAsync(GraphFixture graph, bool deleteApplication)
    {
        await using var reader = await live.OpenAsync();
        Assert.Equal(deleteApplication ? 1 : 2, await reader.Applications.CountAsync());
        Assert.Null(await reader.Authorizations.FindByIdAsync(graph.Authorization.Id!));
        Assert.Equal(1, await reader.Authorizations.CountAsync());
        Assert.Equal(graph.OutsideToken.Id, await reader.Db.Set<Token>().Select(row => row.Id).SingleAsync());
    }

    private async Task AssertCleanAndUnchangedAsync(StoreSession writer, GraphSnapshot before)
    {
        Assert.Null(writer.Db.Database.CurrentTransaction);
        Assert.False(writer.Db.ChangeTracker.HasChanges());
        Assert.Equal(0, await writer.Db.SaveChangesAsync());
        var after = await ReadSnapshotAsync();
        Assert.Equal(before.Applications, after.Applications);
        Assert.Equal(before.Authorizations, after.Authorizations);
        Assert.Equal(before.Tokens, after.Tokens);
    }

    private async Task<GraphSnapshot> ReadSnapshotAsync()
    {
        await using var reader = await live.OpenAsync();
        return new(
            await reader.Db.Set<Application>().OrderBy(row => row.Id)
                .Select(row => new AppSnapshot(row.Id, row.ConcurrencyToken)).ToArrayAsync(),
            await reader.Db.Set<Authorization>().OrderBy(row => row.Id)
                .Select(row => new AuthSnapshot(row.Id, row.ConcurrencyToken, EF.Property<string>(row, "ApplicationId"), row.Status)).ToArrayAsync(),
            await reader.Db.Set<Token>().OrderBy(row => row.Id)
                .Select(row => new TokenSnapshot(row.Id, row.ConcurrencyToken, EF.Property<string>(row, "ApplicationId"),
                    EF.Property<string>(row, "AuthorizationId"), row.Payload, row.Properties)).ToArrayAsync());
    }

    private sealed record GraphFixture(Application App, Authorization Authorization, Token OutsideToken, Token Sample);
    private sealed record GraphSnapshot(AppSnapshot[] Applications, AuthSnapshot[] Authorizations, TokenSnapshot[] Tokens);
    private sealed record AppSnapshot(string? Id, string? Stamp);
    private sealed record AuthSnapshot(string? Id, string? Stamp, string? App, string? Status);
    private sealed record TokenSnapshot(string? Id, string? Stamp, string? App, string? Auth, string? Payload, string? Properties);

    private sealed class GraphSaveProbe(Action<GraphSaveProbe>? afterSave = null) : SaveChangesInterceptor
    {
        private int pendingTokens;
        private Guid? transactionId;

        public int CompletedTokenRows { get; private set; }
        public int TokenPages { get; private set; }
        public int CompletedBatches { get; private set; }
        public int PeakTrackedDependents { get; private set; }
        public long LobBytes { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (!db.ChangeTracker.HasChanges())
                return ValueTask.FromResult(result);

            var transaction = db.Database.CurrentTransaction!;
            Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
            transactionId ??= transaction.TransactionId;
            Assert.Equal(transactionId.Value, transaction.TransactionId);

            var tokens = db.ChangeTracker.Entries<Token>().ToArray();
            var dependents = tokens.Length + db.ChangeTracker.Entries<Authorization>()
                .Count(entry => entry.State == EntityState.Deleted);
            PeakTrackedDependents = Math.Max(PeakTrackedDependents, dependents);
            pendingTokens = tokens.Count(entry => entry.State == EntityState.Deleted);
            LobBytes += tokens.Where(entry => entry.State == EntityState.Deleted)
                .Sum(entry => ((long)(entry.Entity.Payload?.Length ?? 0) + (entry.Entity.Properties?.Length ?? 0)) * sizeof(char));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (result > 0)
            {
                CompletedBatches++;
                if (pendingTokens > 0)
                {
                    TokenPages++;
                    CompletedTokenRows += pendingTokens;
                }
                afterSave?.Invoke(this);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class GraphReadProbe : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public List<string[]> Projections { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Projections.Add(Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray());
            return ValueTask.FromResult(result);
        }
    }
}
