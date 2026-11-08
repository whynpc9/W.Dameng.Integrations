using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Authorization = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization;
using Token = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken;

namespace W.OpenIddict.Dameng.Tests;

public sealed partial class StoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GraphDeletionMeasuresMaterializedLobsAndPagedProjection(bool deleteApplication)
    {
        const int tokenCount = 32;
        const int pageSize = 16;
        await using var seed = await live.OpenAsync();
        await live.ResetAsync(seed.Db);
        var app = await CreateApplication(seed, "graph-probe");
        var authorization = await CreateAuthorization(seed, app.Id);
        var descriptor = new OpenIddictTokenDescriptor
        {
            ApplicationId = app.Id, AuthorizationId = authorization.Id, Subject = Subject,
            Status = Statuses.Valid, Type = TokenTypeHints.AccessToken,
            Payload = new string('p', 32 * 1024),
            Properties = { ["probe"] = JsonSerializer.SerializeToElement(new string('j', 16 * 1024)) }
        };
        var sample = (Token)await seed.Tokens.CreateAsync(descriptor);
        var copied = await seed.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OpenIddictTokens" ("Id", "ApplicationId", "AuthorizationId", "ConcurrencyToken", "Payload", "Properties")
            SELECT 'probe-' || copies."RowNumber", t."ApplicationId", t."AuthorizationId", t."ConcurrencyToken", t."Payload", t."Properties"
            FROM "OpenIddictTokens" t
            CROSS JOIN (SELECT LEVEL AS "RowNumber" FROM DUAL CONNECT BY LEVEL <= {tokenCount - 1}) copies
            WHERE t."Id" = {sample.Id}
            """);
        Assert.Equal(tokenCount - 1, copied);

        // Read-only comparison. These pages cannot establish the semantics of a projected DELETE.
        await using (var reader = await live.OpenAsync())
        {
            var candidates = reader.Db.Set<Token>().AsNoTracking();
            if (deleteApplication)
            {
                var authorizations = reader.Db.Set<Authorization>()
                    .Where(row => EF.Property<string>(row, "ApplicationId") == app.Id);
                candidates = candidates.Where(row => EF.Property<string>(row, "ApplicationId") == app.Id ||
                    authorizations.Any(item => item.Id == EF.Property<string>(row, "AuthorizationId")));
            }
            else
            {
                candidates = candidates.Where(row => EF.Property<string>(row, "AuthorizationId") == authorization.Id);
            }

            await using var transaction = await reader.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var timer = Stopwatch.StartNew();
            var projectedBytes = 0L;
            var ids = new HashSet<string>();
            for (var offset = 0; offset < tokenCount; offset += pageSize)
            {
                var page = await candidates.OrderBy(row => row.Id).Skip(offset).Take(pageSize)
                    .Select(row => new
                    {
                        row.Id, row.ConcurrencyToken,
                        ApplicationId = EF.Property<string>(row, "ApplicationId"),
                        AuthorizationId = EF.Property<string>(row, "AuthorizationId")
                    }).ToListAsync();
                Assert.Equal(pageSize, page.Count);
                foreach (var row in page)
                {
                    Assert.True(ids.Add(row.Id!));
                    Assert.Equal(sample.ConcurrencyToken, row.ConcurrencyToken);
                    Assert.Equal((app.Id, authorization.Id), (row.ApplicationId, row.AuthorizationId));
                    projectedBytes += Utf16Bytes(row.Id, row.ConcurrencyToken, row.ApplicationId, row.AuthorizationId);
                }
            }

            output.WriteLine("projection root={0} rows={1} pages={2} UTF16_field_bytes={3} read_ms={4}",
                deleteApplication ? "application" : "authorization", ids.Count, tokenCount / pageSize,
                projectedBytes, timer.ElapsedMilliseconds);
            Assert.Equal(tokenCount, ids.Count);
            await transaction.RollbackAsync();
        }

        if (deleteApplication)
        {
            var fault = new FailAfterSave();
            await using (var failing = await live.OpenAsync(interceptor: fault))
            {
                var target = (await failing.Applications.FindByIdAsync(app.Id!))!;
                await Assert.ThrowsAsync<RejectedSave>(() => failing.Applications.DeleteAsync(target).AsTask());
                Assert.Equal(tokenCount + 2, fault.RowsWritten);
                Assert.Equal(IsolationLevel.Serializable, fault.Isolation);
                Assert.False(failing.Db.ChangeTracker.HasChanges());
            }

            await using var observer = await live.OpenAsync();
            Assert.NotNull(await observer.Applications.FindByIdAsync(app.Id!));
            Assert.NotNull(await observer.Authorizations.FindByIdAsync(authorization.Id!));
            Assert.Equal(tokenCount, await observer.Tokens.CountAsync());
            var persisted = (await observer.Tokens.FindByIdAsync(sample.Id!))!;
            Assert.Equal(sample.Payload, await observer.Tokens.GetPayloadAsync(persisted));
        }

        var observed = new GraphSaveProbe();
        await using (var writer = await live.OpenAsync(interceptor: observed))
        {
            var root = deleteApplication
                ? await writer.Applications.FindByIdAsync(app.Id!)
                : await writer.Authorizations.FindByIdAsync(authorization.Id!);
            Assert.NotNull(root);
            observed.Timer.Start();
            if (deleteApplication)
                await writer.Applications.DeleteAsync(root);
            else
                await writer.Authorizations.DeleteAsync(root);
            observed.Timer.Stop();
        }

        Assert.Equal(IsolationLevel.Serializable, observed.Isolation);
        Assert.Equal(tokenCount, observed.Tokens);
        Assert.Equal(tokenCount * Utf16Bytes(sample.Payload, sample.Properties), observed.LobBytes);
        output.WriteLine("tracked root={0} rows={1} UTF16_LOB_bytes={2} stage_ms={3} delete_ms={4}",
            deleteApplication ? "application" : "authorization", observed.Tokens,
            observed.LobBytes, observed.StageMilliseconds, observed.Timer.ElapsedMilliseconds);

        await using var result = await live.OpenAsync();
        Assert.Equal(deleteApplication ? 0 : 1, await result.Applications.CountAsync());
        Assert.Equal(0, await result.Authorizations.CountAsync());
        Assert.Equal(0, await result.Tokens.CountAsync());
    }

    private static long Utf16Bytes(params string?[] fields) => fields.Sum(field => (long)(field?.Length ?? 0)) * sizeof(char);

    private sealed class GraphSaveProbe : SaveChangesInterceptor
    {
        public Stopwatch Timer { get; } = new();
        public long StageMilliseconds { get; private set; }
        public int Tokens { get; private set; }
        public long LobBytes { get; private set; }
        public IsolationLevel? Isolation { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            StageMilliseconds = Timer.ElapsedMilliseconds;
            var db = eventData.Context!;
            Isolation = db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
            var entries = db.ChangeTracker.Entries<Token>().Where(entry => entry.State == EntityState.Deleted).ToArray();
            Tokens = entries.Length;
            LobBytes = entries.Sum(entry => Utf16Bytes(entry.Entity.Payload, entry.Entity.Properties));
            return ValueTask.FromResult(result);
        }
    }
}
