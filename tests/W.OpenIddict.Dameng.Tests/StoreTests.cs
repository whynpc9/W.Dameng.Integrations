using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Abstractions.OpenIddictExceptions;
using Application = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication;
using Authorization = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization;
using Scope = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreScope;
using Token = OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken;

namespace W.OpenIddict.Dameng.Tests;

public sealed partial class StoreTests(LiveDatabase live, ITestOutputHelper output) : IClassFixture<LiveDatabase>
{
    private static readonly DateTimeOffset Old = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Future = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string LongText = string.Concat(Enumerable.Repeat("达梦🙂", 16_000));
    private static readonly string Subject = new('中', 400);

    [Fact]
    public async Task ResetRejectsAnAlternateSchemaOwnedByTheSameUser()
    {
        await using var session = await live.OpenAsync();
        var db = session.Db;
        var alternate = "OID_GUARD_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        // Both schemas belong to this fixture's user. The alternate schema has a same-named
        // table so an unchecked first DELETE would succeed and remove the sentinel.
#pragma warning disable EF1002 // Schema identifier is generated above from a hexadecimal GUID.
        await db.Database.ExecuteSqlRawAsync($"""CREATE SCHEMA "{alternate}" """);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"""SET SCHEMA "{alternate}" """);
            await db.Database.ExecuteSqlRawAsync("""CREATE TABLE "OpenIddictTokens" ("Sentinel" INT)""");
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "OpenIddictTokens" VALUES (73)""");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => live.ResetAsync(db));
            Assert.Contains("Current user, schema or tablespace", exception.Message);
            await using var reader = await live.OpenAsync();
            Assert.Equal(73, await reader.Db.Database.SqlQueryRaw<int>(
                $"""SELECT "Sentinel" AS "Value" FROM "{alternate}"."OpenIddictTokens" """).SingleAsync());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
            await using var cleanup = await live.OpenAsync();
            await cleanup.Db.Database.ExecuteSqlRawAsync($"""DROP SCHEMA "{alternate}" CASCADE""");
        }
#pragma warning restore EF1002
    }

    [Fact]
    public async Task NativeCrudAndLobRoundTrip()
    {
        await using var writer = await live.OpenAsync();
        await live.ResetAsync(writer.Db);
        var properties = JsonSerializer.SerializeToElement(LongText);
        const string redirect = "https://example.test/callback";
        const string logout = "https://example.test/signed-out";
        var app = (Application)await writer.Applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = "interactive", ClientType = ClientTypes.Public,
            RedirectUris = { new Uri(redirect), new Uri(redirect + "/second") },
            PostLogoutRedirectUris = { new Uri(logout) },
            Properties = { ["large"] = properties }
        });
        var scope = (Scope)await writer.Scopes.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = "records", Resources = { "records-api" }, Properties = { ["large"] = properties }
        });
        var authorization = (Authorization)await writer.Authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = app.Id, Subject = Subject, Status = Statuses.Valid,
            Type = AuthorizationTypes.Permanent, CreationDate = Old, Scopes = { "records" },
            Properties = { ["large"] = properties }
        });
        var token = (Token)await writer.Tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            ApplicationId = app.Id, AuthorizationId = authorization.Id, Subject = Subject,
            Type = TokenTypeHints.AccessToken, Status = Statuses.Valid,
            CreationDate = Old.AddTicks(7), ExpirationDate = Future,
            Payload = LongText, ReferenceId = "private-reference",
            Properties = { ["large"] = properties }
        });

        await using (var reader = await live.OpenAsync())
        {
            var readApp = Assert.IsType<Application>(await reader.Applications.FindByClientIdAsync("interactive"));
            Assert.Equal(app.Id, readApp.Id);
            Assert.Equal(app.Id, ApplicationId(await reader.Applications.FindByRedirectUriAsync(redirect).ToListAsync()));
            Assert.Equal(app.Id, ApplicationId(await reader.Applications.FindByPostLogoutRedirectUriAsync(logout).ToListAsync()));
            Assert.Empty(await reader.Applications.FindByRedirectUriAsync(redirect + "/sec").ToListAsync());
            Assert.Empty(await reader.Applications.FindByPostLogoutRedirectUriAsync(logout.ToUpperInvariant()).ToListAsync());
            Assert.Equal(LongText, (await reader.Applications.GetPropertiesAsync(readApp))["large"].GetString());
            Assert.Equal(scope.Id, (await reader.Scopes.FindByNameAsync("records") as Scope)?.Id);
            var readScope = Assert.IsType<Scope>(await reader.Scopes.FindByIdAsync(scope.Id!));
            Assert.Equal(LongText, (await reader.Scopes.GetPropertiesAsync(readScope))["large"].GetString());
            Assert.Equal(scope.Id, Assert.IsType<Scope>(
                Assert.Single(await reader.Scopes.FindByResourceAsync("records-api").ToListAsync())).Id);
            var readAuthorization = Assert.IsType<Authorization>(await reader.Authorizations.FindByIdAsync(authorization.Id!));
            Assert.Equal(LongText, (await reader.Authorizations.GetPropertiesAsync(readAuthorization))["large"].GetString());
            Assert.Equal(Subject, readAuthorization.Subject);
            var readToken = Assert.IsType<Token>(await reader.Tokens.FindByReferenceIdAsync("private-reference"));
            Assert.Equal(token.Id, readToken.Id);
            Assert.Equal(LongText, await reader.Tokens.GetPayloadAsync(readToken));
            Assert.Equal(LongText, (await reader.Tokens.GetPropertiesAsync(readToken))["large"].GetString());
            Assert.Equal(Old.AddTicks(7), await reader.Tokens.GetCreationDateAsync(readToken));
        }

        app.DisplayName = "application updated";
        scope.Description = "scope updated";
        authorization.Status = Statuses.Inactive;
        token.Payload = LongText + "-updated";
        await writer.Applications.UpdateAsync(app);
        await writer.Scopes.UpdateAsync(scope);
        await writer.Authorizations.UpdateAsync(authorization);
        await writer.Tokens.UpdateAsync(token);
        await using (var reader = await live.OpenAsync())
        {
            Assert.Equal("application updated", (await reader.Db.Set<Application>().SingleAsync()).DisplayName);
            Assert.Equal("scope updated", (await reader.Db.Set<Scope>().SingleAsync()).Description);
            Assert.Equal(Statuses.Inactive, (await reader.Db.Set<Authorization>().SingleAsync()).Status);
            Assert.Equal(LongText + "-updated", (await reader.Db.Set<Token>().SingleAsync()).Payload);
        }

        await writer.Tokens.DeleteAsync(token);
        await writer.Authorizations.DeleteAsync(authorization);
        await writer.Scopes.DeleteAsync(scope);
        await writer.Applications.DeleteAsync(app);
        await using var empty = await live.OpenAsync();
        Assert.Equal(0, await empty.Tokens.CountAsync());
        Assert.Equal(0, await empty.Authorizations.CountAsync());
        Assert.Equal(0, await empty.Scopes.CountAsync());
        Assert.Equal(0, await empty.Applications.CountAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CascadeRejectsStaleSnapshotsAndCommitsAllDependents(bool deleteApplication, bool staleDependent)
    {
        await using var writer = await live.OpenAsync();
        await live.ResetAsync(writer.Db);
        var app = await CreateApplication(writer, "target");
        var other = await CreateApplication(writer, "other");
        var auth = await CreateAuthorization(writer, app.Id);
        var authOfOther = await CreateAuthorization(writer, other.Id);
        var children = new[]
        {
            await CreateToken(writer, app.Id, auth.Id),
            await CreateToken(writer, other.Id, auth.Id),
            await CreateToken(writer, null, auth.Id)
        };
        var direct = await CreateToken(writer, app.Id, authOfOther.Id);
        var outside = await CreateToken(writer, other.Id, authOfOther.Id);

        await using (var loser = await live.OpenAsync())
        {
            var staleApp = await loser.Applications.FindByIdAsync(app.Id!);
            var staleAuth = await loser.Authorizations.FindByIdAsync(auth.Id!);
            if (staleDependent)
            {
                Assert.IsType<Token>(await loser.Tokens.FindByIdAsync(children[0].Id!));
                children[0].Payload = "updated dependent";
                await writer.Tokens.UpdateAsync(children[0]);
            }
            else if (deleteApplication)
            {
                app.DisplayName = "updated by another session";
                await writer.Applications.UpdateAsync(app);
            }
            else
            {
                auth.Status = Statuses.Revoked;
                await writer.Authorizations.UpdateAsync(auth);
            }

            await Assert.ThrowsAsync<ConcurrencyException>(() => deleteApplication
                ? loser.Applications.DeleteAsync(staleApp!).AsTask()
                : loser.Authorizations.DeleteAsync(staleAuth!).AsTask());
            Assert.False(loser.Db.ChangeTracker.HasChanges());
            Assert.Equal(0, await loser.Db.SaveChangesAsync());
        }

        await using (var reader = await live.OpenAsync())
        {
            Assert.Equal(2, await reader.Applications.CountAsync());
            Assert.Equal(2, await reader.Authorizations.CountAsync());
            Assert.Equal(5, await reader.Tokens.CountAsync());
            if (staleDependent)
                Assert.Equal("updated dependent", await reader.Tokens.GetPayloadAsync(
                    (await reader.Tokens.FindByIdAsync(children[0].Id!))!));
        }

        await using (var retry = await live.OpenAsync())
        {
            if (deleteApplication)
                await retry.Applications.DeleteAsync((await retry.Applications.FindByIdAsync(app.Id!))!);
            else
                await retry.Authorizations.DeleteAsync((await retry.Authorizations.FindByIdAsync(auth.Id!))!);
        }

        await using var result = await live.OpenAsync();
        Assert.Equal(deleteApplication ? 1 : 2, await result.Applications.CountAsync());
        Assert.Equal(1, await result.Authorizations.CountAsync());
        Assert.NotNull(await result.Authorizations.FindByIdAsync(authOfOther.Id!));
        var ids = await result.Db.Set<Token>().Select(token => token.Id).ToListAsync();
        Assert.All(children, child => Assert.DoesNotContain(child.Id, ids));
        Assert.Contains(outside.Id, ids);
        Assert.Equal(!deleteApplication, ids.Contains(direct.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllRevokeRoutesAndStaleWriterProtection(bool tracked)
    {
        await using var writer = await live.OpenAsync(tracked);
        await live.ResetAsync(writer.Db);
        var app = await CreateApplication(writer, "selected");
        var byApplication = await CreateApplication(writer, "by-application");
        var other = await CreateApplication(writer, "other");
        var auth = await CreateAuthorization(writer, app.Id);
        var applicationAuth = await CreateAuthorization(writer, byApplication.Id);
        var subjectAuth = await CreateAuthorization(writer, other.Id, "auth-subject");
        var outsideAuth = await CreateAuthorization(writer, other.Id, "outside");
        var token = await CreateToken(writer, app.Id, auth.Id);
        var applicationToken = await CreateToken(writer, byApplication.Id, applicationAuth.Id);
        var authorizationToken = await CreateToken(writer, other.Id, subjectAuth.Id);
        var subjectToken = await CreateToken(writer, other.Id, outsideAuth.Id, "token-subject");
        var outsideToken = await CreateToken(writer, other.Id, outsideAuth.Id, "outside");

        await using var stale = await live.OpenAsync();
        var oldToken = Assert.IsType<Token>(await stale.Tokens.FindByIdAsync(token.Id!));
        var oldAuthorization = Assert.IsType<Authorization>(await stale.Authorizations.FindByIdAsync(auth.Id!));

        Assert.Equal(1, await writer.Tokens.RevokeAsync(Subject, app.Id, Statuses.Valid, TokenTypeHints.AccessToken));
        Assert.Equal(1, await writer.Tokens.RevokeByApplicationIdAsync(byApplication.Id!));
        Assert.Equal(1, await writer.Tokens.RevokeByAuthorizationIdAsync(subjectAuth.Id!));
        Assert.Equal(1, await writer.Tokens.RevokeBySubjectAsync("token-subject"));
        Assert.Equal(0, await writer.Tokens.RevokeAsync(Subject, app.Id, Statuses.Valid, TokenTypeHints.AccessToken));

        Assert.Equal(1, await writer.Authorizations.RevokeAsync(Subject, app.Id, Statuses.Valid, AuthorizationTypes.Permanent));
        Assert.Equal(1, await writer.Authorizations.RevokeByApplicationIdAsync(byApplication.Id!));
        Assert.Equal(1, await writer.Authorizations.RevokeBySubjectAsync("auth-subject"));
        Assert.Equal(0, await writer.Authorizations.RevokeAsync(Subject, app.Id, Statuses.Valid, AuthorizationTypes.Permanent));

        oldToken.Payload = "stale";
        await Assert.ThrowsAsync<ConcurrencyException>(() => stale.Tokens.UpdateAsync(oldToken).AsTask());
        oldAuthorization.Properties = "{}";
        await Assert.ThrowsAsync<ConcurrencyException>(() => stale.Authorizations.UpdateAsync(oldAuthorization).AsTask());

        await using var reader = await live.OpenAsync();
        var tokenStatuses = await reader.Db.Set<Token>().ToDictionaryAsync(row => row.Id!, row => row.Status);
        var authStatuses = await reader.Db.Set<Authorization>().ToDictionaryAsync(row => row.Id!, row => row.Status);
        Assert.All(new[] { token, applicationToken, authorizationToken, subjectToken },
            row => Assert.Equal(Statuses.Revoked, tokenStatuses[row.Id!]));
        Assert.All(new[] { auth, applicationAuth, subjectAuth },
            row => Assert.Equal(Statuses.Revoked, authStatuses[row.Id!]));
        Assert.Equal(Statuses.Valid, tokenStatuses[outsideToken.Id!]);
        Assert.Equal(Statuses.Valid, authStatuses[outsideAuth.Id!]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PruneAppliesPredicatesAndCountsCommittedRows(bool tracked)
    {
        await using var writer = await live.OpenAsync(tracked);
        await live.ResetAsync(writer.Db);
        var app = await CreateApplication(writer, "prune");
        var revoked = await CreateAuthorization(writer, app.Id, status: Statuses.Revoked);
        var permanent = await CreateAuthorization(writer, app.Id);
        var attached = await CreateAuthorization(writer, app.Id, type: AuthorizationTypes.AdHoc);
        await CreateAuthorization(writer, app.Id, type: AuthorizationTypes.AdHoc);
        await CreateAuthorization(writer, app.Id, status: Statuses.Revoked, creation: Future);
        await CreateToken(writer, app.Id, revoked.Id);
        await CreateToken(writer, app.Id, permanent.Id, expires: Old.AddHours(1));
        await CreateToken(writer, app.Id, permanent.Id, status: Statuses.Revoked);
        var active = await CreateToken(writer, app.Id, permanent.Id);
        var inactive = await CreateToken(writer, app.Id, permanent.Id, status: Statuses.Inactive);
        var linked = await CreateToken(writer, app.Id, attached.Id);
        var recent = await CreateToken(writer, app.Id, revoked.Id, status: Statuses.Revoked, creation: Future);
        await CreateAuthorization(writer, app.Id, status: Statuses.Revoked);

        const int beyondBatch = 1_001;
        if (!tracked)
        {
            // Set-based fixture rows force a second page without thousands of individual inserts.
            await writer.Db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "OpenIddictTokens" ("Id", "CreationDate", "Status")
                SELECT 'bulk-token-' || LEVEL, TIMESTAMP '2020-01-01 00:00:00', 'revoked'
                FROM DUAL CONNECT BY LEVEL <= 1001
                """);
            await writer.Db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "OpenIddictAuthorizations" ("Id", "CreationDate", "Status", "Type")
                SELECT 'bulk-auth-' || LEVEL, TIMESTAMP '2020-01-01 00:00:00', 'revoked', 'ad_hoc'
                FROM DUAL CONNECT BY LEVEL <= 1001
                """);
        }

        var threshold = Old.AddDays(1).ToOffset(TimeSpan.FromHours(8));
        Assert.Equal(3 + (tracked ? 0 : beyondBatch), await writer.Tokens.PruneAsync(threshold));
        Assert.Equal(2 + (tracked ? 0 : beyondBatch), await writer.Authorizations.PruneAsync(threshold));
        Assert.Equal(0, await writer.Tokens.PruneAsync(threshold));
        Assert.Equal(0, await writer.Authorizations.PruneAsync(threshold));

        await using var reader = await live.OpenAsync();
        var tokenIds = await reader.Db.Set<Token>().Select(token => token.Id).ToListAsync();
        Assert.Equal(new[] { active.Id, inactive.Id, linked.Id, recent.Id }.Order(), tokenIds.Order());
        Assert.Equal(4, await reader.Authorizations.CountAsync());
        Assert.NotNull(await reader.Authorizations.FindByIdAsync(revoked.Id!));
        Assert.NotNull(await reader.Authorizations.FindByIdAsync(attached.Id!));
    }

    [Fact]
    public async Task SerializableTrackedSaveHasIndependentCommitAndRollbackReadback()
    {
        await using var writer = await live.OpenAsync();
        await live.ResetAsync(writer.Db);
        var app = await CreateApplication(writer, "transaction");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            writer.Db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead));

        await using (var transaction = await writer.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
            app.DisplayName = "rolled back";
            await writer.Applications.UpdateAsync(app);
            await using var observer = await live.OpenAsync();
            Assert.Null((await observer.Db.Set<Application>().SingleAsync()).DisplayName);
            await transaction.RollbackAsync();
        }

        await using var retry = await live.OpenAsync();
        var persisted = Assert.IsType<Application>(await retry.Applications.FindByIdAsync(app.Id!));
        Assert.Null(persisted.DisplayName);
        await using (var transaction = await retry.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            persisted.DisplayName = "committed";
            await retry.Applications.UpdateAsync(persisted);
            await transaction.CommitAsync();
        }

        await using var reader = await live.OpenAsync();
        Assert.Equal("committed", (await reader.Db.Set<Application>().SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task AdapterStopsWhenBeginFailsAndNativeCascadeCanPartiallyDelete()
    {
        await using var seed = await live.OpenAsync();
        await live.ResetAsync(seed.Db);
        var app = await CreateApplication(seed, "begin-failure");
        var token = await CreateToken(seed, app.Id, null, status: Statuses.Revoked);
        var adapterFault = new RejectSerializable();
        await using (var adapted = await live.OpenAsync(tracked: true, interceptor: adapterFault))
        {
            var target = Assert.IsType<Application>(await adapted.Applications.FindByIdAsync(app.Id!));
            adapted.Db.ChangeTracker.AutoDetectChangesEnabled = false;
            target.DisplayName = "pending change";
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapted.Applications.DeleteAsync(target).AsTask());
            Assert.Equal(0, adapterFault.Attempts);
            await adapted.Db.Entry(target).ReloadAsync();
            await Assert.ThrowsAsync<RejectedTransaction>(() => adapted.Applications.DeleteAsync(target).AsTask());
            await Assert.ThrowsAsync<RejectedTransaction>(() => adapted.Tokens.PruneAsync(Old.AddDays(1)).AsTask());
            Assert.Equal(2, adapterFault.Attempts);
        }

        await using (var reader = await live.OpenAsync())
        {
            Assert.NotNull(await reader.Applications.FindByIdAsync(app.Id!));
            Assert.NotNull(await reader.Tokens.FindByIdAsync(token.Id!));
        }

        // Reproduce the upstream 6.3.0 null-transaction continuation using a stale root.
        var nativeFault = new RejectSerializable();
        await using (var native = await live.OpenAsync(interceptor: nativeFault, native: true))
        {
            var stale = (await native.Applications.FindByIdAsync(app.Id!))!;
            app.DisplayName = "concurrent update";
            await seed.Applications.UpdateAsync(app);
            await Assert.ThrowsAsync<ConcurrencyException>(() => native.Applications.DeleteAsync(stale).AsTask());
            Assert.Equal(1, nativeFault.Attempts);
        }

        await using var partial = await live.OpenAsync();
        Assert.NotNull(await partial.Applications.FindByIdAsync(app.Id!));
        Assert.Null(await partial.Tokens.FindByIdAsync(token.Id!));
    }

    [Fact]
    public async Task TrackedPruneRollsBackAfterSaveAndRecoversOnRetry()
    {
        await using var writer = await live.OpenAsync();
        await live.ResetAsync(writer.Db);
        await CreateToken(writer, null, null, status: Statuses.Revoked);
        await CreateToken(writer, null, null, status: Statuses.Revoked);
        var fault = new FailAfterSave();
        var threshold = Old.AddDays(1);

        await using (var failing = await live.OpenAsync(tracked: true, interceptor: fault))
        {
            await Assert.ThrowsAsync<RejectedSave>(() => failing.Tokens.PruneAsync(threshold).AsTask());
            Assert.Equal(2, fault.RowsWritten);
            Assert.Equal(IsolationLevel.Serializable, fault.Isolation);
            Assert.False(failing.Db.ChangeTracker.HasChanges());
        }

        await using (var retry = await live.OpenAsync(tracked: true))
        {
            Assert.Equal(2, await retry.Tokens.CountAsync());
            Assert.Equal(2, await retry.Tokens.PruneAsync(threshold));
        }

        await using var reader = await live.OpenAsync();
        Assert.Equal(0, await reader.Tokens.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterDmlRollsBackAndAllowsRetry(bool deleteGraph)
    {
        await using var seed = await live.OpenAsync();
        await live.ResetAsync(seed.Db);
        var app = await CreateApplication(seed, "cancel");
        var auth = await CreateAuthorization(seed, app.Id);
        var first = await CreateToken(seed, app.Id, auth.Id);
        var second = await CreateToken(seed, app.Id, null);

        using var cancellation = new CancellationTokenSource();
        var cancelAfterSave = new CancelAfterSave(cancellation);
        await using (var failing = await live.OpenAsync(tracked: true, interceptor: cancelAfterSave))
        {
            var target = Assert.IsType<Application>(await failing.Applications.FindByIdAsync(app.Id!));
            Task MaintainAsync(CancellationToken token) => deleteGraph
                ? failing.Applications.DeleteAsync(target, token).AsTask()
                : failing.Tokens.RevokeBySubjectAsync(Subject, token).AsTask();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MaintainAsync(new CancellationToken(canceled: true)));
            Assert.Equal(0, cancelAfterSave.RowsWritten);
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MaintainAsync(cancellation.Token));
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.Equal(2, cancelAfterSave.RowsWritten);
            Assert.Equal(IsolationLevel.Serializable, cancelAfterSave.Isolation);
            Assert.Null(failing.Db.Database.CurrentTransaction);
            Assert.False(failing.Db.ChangeTracker.HasChanges());
            Assert.Equal(0, await failing.Db.SaveChangesAsync());
        }

        await using (var observer = await live.OpenAsync())
        {
            Assert.NotNull(await observer.Applications.FindByIdAsync(app.Id!));
            Assert.NotNull(await observer.Authorizations.FindByIdAsync(auth.Id!));
            var tokens = await observer.Db.Set<Token>().AsNoTracking().ToDictionaryAsync(row => row.Id!);
            Assert.Equal(2, tokens.Count);
            Assert.Equal((Statuses.Valid, first.ConcurrencyToken), (tokens[first.Id!].Status, tokens[first.Id!].ConcurrencyToken));
            Assert.Equal((Statuses.Valid, second.ConcurrencyToken), (tokens[second.Id!].Status, tokens[second.Id!].ConcurrencyToken));
        }

        await using (var retry = await live.OpenAsync(tracked: true))
        {
            if (deleteGraph)
                await retry.Applications.DeleteAsync((await retry.Applications.FindByIdAsync(app.Id!))!);
            else
                Assert.Equal(2, await retry.Tokens.RevokeBySubjectAsync(Subject));
        }

        await using var reader = await live.OpenAsync();
        if (deleteGraph)
        {
            Assert.Equal(0, await reader.Applications.CountAsync());
            Assert.Equal(0, await reader.Authorizations.CountAsync());
            Assert.Equal(0, await reader.Tokens.CountAsync());
        }
        else
        {
            var rows = await reader.Db.Set<Token>().Select(row => row.Status).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, status => Assert.Equal(Statuses.Revoked, status));
        }
    }

    private sealed class CancelAfterSave(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public int RowsWritten { get; private set; }
        public IsolationLevel? Isolation { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            // A subsequent zero-row SaveChanges in the same scope must remain harmless.
            if (result > 0)
            {
                RowsWritten = result;
                Isolation = eventData.Context?.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
                source.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectedSave : InvalidOperationException;

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public int RowsWritten { get; private set; }
        public IsolationLevel? Isolation { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            RowsWritten = result;
            Isolation = eventData.Context?.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
            throw new RejectedSave();
        }
    }

    private static string? ApplicationId(List<object> applications) => Assert.IsType<Application>(Assert.Single(applications)).Id;

    private static async ValueTask<Application> CreateApplication(StoreSession session, string clientId) =>
        (Application)await session.Applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId, ClientType = ClientTypes.Public
        });

    private static async ValueTask<Authorization> CreateAuthorization(StoreSession session, string? app,
        string? subject = null, string status = Statuses.Valid, string type = AuthorizationTypes.Permanent,
        DateTimeOffset? creation = null) =>
        (Authorization)await session.Authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = app, Subject = subject ?? Subject, Status = status, Type = type,
            CreationDate = creation ?? Old
        });

    private static async ValueTask<Token> CreateToken(StoreSession session, string? app, string? authorization,
        string? subject = null, string status = Statuses.Valid, DateTimeOffset? expires = null,
        DateTimeOffset? creation = null) =>
        (Token)await session.Tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            ApplicationId = app, AuthorizationId = authorization, Subject = subject ?? Subject, Status = status,
            Type = TokenTypeHints.AccessToken, CreationDate = creation ?? Old, ExpirationDate = expires ?? Future
        });

    private sealed class RejectedTransaction : InvalidOperationException;

    private sealed class RejectSerializable : DbTransactionInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.IsolationLevel == IsolationLevel.Serializable)
            {
                Attempts++;
                throw new RejectedTransaction();
            }
            return ValueTask.FromResult(result);
        }
    }
}
