using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using W.Dm;
using Xunit;

namespace W.Dameng.Identity.Tests;

public sealed class IdentityTests(IdentityDatabase database) : IClassFixture<IdentityDatabase>
{
    private const int UniqueViolation = -6602;
    private const string Password = "Correct-horse9!";
    private const string InitialMigration = "InitialIdentity";

    [Fact]
    public async Task MigrationsCreateIdentitySchemaAndCanBeReapplied()
    {
        await using var scope = database.NewScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.EndsWith("_" + InitialMigration, migration, StringComparison.Ordinal);
        await context.Database.MigrateAsync();
        Assert.Equal(migration, Assert.Single(await context.Database.GetAppliedMigrationsAsync()));

        using var connection = database.OpenReadback();
        foreach (var table in new[]
        {
            "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetRoleClaims",
            "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "__EFMigrationsHistory"
        })
        {
            Assert.Equal(1, Count(connection,
                "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :p0 AND TABLESPACE_NAME = :p1",
                table, database.Tablespace));
        }

        Assert.Equal(migration, Scalar(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task NativeManagersPersistAndFindUsersAndPasswords()
    {
        await using var writer = database.NewScope();
        var users = writer.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.IsType<UserStore<IdentityUser, IdentityRole, IdentityContext, string,
            IdentityUserClaim<string>, IdentityUserRole<string>, IdentityUserLogin<string>,
            IdentityUserToken<string>, IdentityRoleClaim<string>, IdentityUserPasskey<string>>>(
            writer.ServiceProvider.GetRequiredService<IUserStore<IdentityUser>>());
        Assert.IsType<RoleStore<IdentityRole, IdentityContext, string, IdentityUserRole<string>, IdentityRoleClaim<string>>>(
            writer.ServiceProvider.GetRequiredService<IRoleStore<IdentityRole>>());
        var user = NewUser();
        Succeeded(await users.CreateAsync(user, Password));

        using var connection = database.OpenReadback();
        Assert.Equal(user.UserName!.ToUpperInvariant(), Scalar(connection,
            """SELECT "NormalizedUserName" FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        var hash = Assert.IsType<string>(Scalar(connection,
            """SELECT "PasswordHash" FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        Assert.NotEqual(Password, hash);

        await using var reader = database.NewScope();
        var independentUsers = reader.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var found = await independentUsers.FindByNameAsync(user.UserName.ToUpperInvariant());
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.Id);
        Assert.Equal(user.Id, (await independentUsers.FindByEmailAsync(user.Email!.ToUpperInvariant()))?.Id);
        Assert.Equal(user.Id, (await independentUsers.FindByIdAsync(user.Id))?.Id);
        Assert.True(await independentUsers.CheckPasswordAsync(found, Password));
        Assert.False(await independentUsers.CheckPasswordAsync(found, Password + "wrong"));
    }

    [Fact]
    public async Task ManagersRoundTripRolesClaimsLoginsTokensAndLongValues()
    {
        await using var writer = database.NewScope();
        var users = writer.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = writer.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = NewUser(userNameLength: 256);
        var role = new IdentityRole(Unique("Role").PadRight(256, 'r'));
        var payload = string.Concat(Enumerable.Repeat("身份🔐abc", 1600));
        var userClaim = new Claim(Unique("urn:identity:user:"), payload);
        var roleClaim = new Claim("urn:identity:role", payload + "r");
        var provider = Unique("Provider").PadRight(128, 'p');
        var key = Unique("Key").PadRight(128, 'k');
        var tokenName = Unique("Token").PadRight(128, 't');
        var login = new UserLoginInfo(provider, key, "外部认证");

        Succeeded(await users.CreateAsync(user));
        Succeeded(await roles.CreateAsync(role));
        Succeeded(await users.AddToRoleAsync(user, role.Name!));
        Succeeded(await users.AddClaimAsync(user, userClaim));
        Succeeded(await roles.AddClaimAsync(role, roleClaim));
        Succeeded(await users.AddLoginAsync(user, login));
        Succeeded(await users.SetAuthenticationTokenAsync(user, provider, tokenName, payload));
        Succeeded(await users.SetAuthenticationTokenAsync(user, provider, tokenName, payload + "2"));

        await using var reader = database.NewScope();
        var readUsers = reader.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var readRoles = reader.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var foundUser = await readUsers.FindByLoginAsync(provider, key);
        Assert.NotNull(foundUser);
        Assert.Equal(user.Id, foundUser.Id);
        Assert.Equal(user.UserName, foundUser.UserName);
        var foundRole = await readRoles.FindByNameAsync(role.Name!.ToUpperInvariant());
        Assert.NotNull(foundRole);
        Assert.Equal(role.Id, foundRole.Id);
        Assert.Equal(role.Name, foundRole.Name);
        Assert.True(await readUsers.IsInRoleAsync(foundUser, role.Name));
        Assert.Equal(user.Id, Assert.Single(await readUsers.GetUsersInRoleAsync(role.Name)).Id);
        Assert.Equal(payload, Assert.Single(await readUsers.GetClaimsAsync(foundUser)).Value);
        Assert.Equal(user.Id, Assert.Single(await readUsers.GetUsersForClaimAsync(userClaim)).Id);
        Assert.Equal(payload + "r", Assert.Single(await readRoles.GetClaimsAsync(foundRole)).Value);
        Assert.Equal(login.ProviderDisplayName, Assert.Single(await readUsers.GetLoginsAsync(foundUser)).ProviderDisplayName);
        Assert.Equal(payload + "2", await readUsers.GetAuthenticationTokenAsync(foundUser, provider, tokenName));

        using var connection = database.OpenReadback();
        Assert.Equal(1, Count(connection,
            """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId" = :p0 AND "RoleId" = :p1""", user.Id, role.Id));
        Assert.Equal(1, Count(connection,
            """SELECT COUNT(*) FROM "AspNetUserLogins" WHERE "LoginProvider" = :p0 AND "ProviderKey" = :p1 AND "UserId" = :p2""",
            provider, key, user.Id));
        Assert.Equal(payload + "2", Scalar(connection,
            """SELECT "Value" FROM "AspNetUserTokens" WHERE "UserId" = :p0 AND "LoginProvider" = :p1 AND "Name" = :p2""",
            user.Id, provider, tokenName));
        AssertUniqueViolation(connection,
            """INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES (:p0, :p1)""", user.Id, role.Id);
        AssertUniqueViolation(connection,
            """INSERT INTO "AspNetUserLogins" ("LoginProvider", "ProviderKey", "UserId") VALUES (:p0, :p1, :p2)""",
            provider, key, user.Id);
        AssertUniqueViolation(connection,
            """INSERT INTO "AspNetUserTokens" ("UserId", "LoginProvider", "Name") VALUES (:p0, :p1, :p2)""",
            user.Id, provider, tokenName);
    }

    [Fact]
    public async Task DatabaseEnforcesNormalizedNameIndexesBeyondManagerValidation()
    {
        await using var writer = database.NewScope();
        var users = writer.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = writer.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = NewUser();
        var role = new IdentityRole(Unique("Role"));
        Succeeded(await users.CreateAsync(user));
        Succeeded(await roles.CreateAsync(role));

        var duplicateUser = NewUser();
        duplicateUser.UserName = user.UserName!.ToUpperInvariant();
        AssertError(await users.CreateAsync(duplicateUser), "DuplicateUserName");
        AssertError(await roles.CreateAsync(new IdentityRole(role.Name!.ToUpperInvariant())), "DuplicateRoleName");

        // Bypass manager validation so a failed index migration cannot pass this test.
        using var connection = database.OpenReadback();
        AssertUniqueViolation(connection,
            """INSERT INTO "AspNetUsers" ("Id", "NormalizedUserName", "AccessFailedCount", "EmailConfirmed", "LockoutEnabled", "PhoneNumberConfirmed", "TwoFactorEnabled") VALUES (:p0, :p1, 0, 0, 0, 0, 0)""",
            duplicateUser.Id, user.NormalizedUserName!);
        AssertUniqueViolation(connection,
            """INSERT INTO "AspNetRoles" ("Id", "NormalizedName") VALUES (:p0, :p1)""",
            Guid.NewGuid().ToString(), role.NormalizedName!);
        Assert.Equal(1, Count(connection,
            """SELECT COUNT(*) FROM "AspNetUsers" WHERE "NormalizedUserName" = :p0""", user.NormalizedUserName!));
        Assert.Equal(1, Count(connection,
            """SELECT COUNT(*) FROM "AspNetRoles" WHERE "NormalizedName" = :p0""", role.NormalizedName!));
    }

    [Fact]
    public async Task StaleUserAndRoleWritesFailAndFreshRetriesSucceed()
    {
        await using var writer = database.NewScope();
        await using var stale = database.NewScope();
        var users = writer.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = writer.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var staleUsers = stale.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var staleRoles = stale.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = NewUser();
        var role = new IdentityRole(Unique("Role"));
        Succeeded(await users.CreateAsync(user));
        Succeeded(await roles.CreateAsync(role));

        var oldUser = (await staleUsers.FindByIdAsync(user.Id))!;
        var oldRole = (await staleRoles.FindByIdAsync(role.Id))!;
        var oldUserStamp = oldUser.ConcurrencyStamp;
        var oldRoleStamp = oldRole.ConcurrencyStamp;

        var winnerEmail = Unique("Winner") + "@example.test";
        var winnerRole = Unique("WinnerRole");
        Succeeded(await users.SetEmailAsync(user, winnerEmail));
        Succeeded(await roles.SetRoleNameAsync(role, winnerRole));
        Succeeded(await roles.UpdateAsync(role));

        AssertError(await staleUsers.SetEmailAsync(oldUser, "stale@example.test"), "ConcurrencyFailure");
        Succeeded(await staleRoles.SetRoleNameAsync(oldRole, Unique("StaleRole")));
        AssertError(await staleRoles.UpdateAsync(oldRole), "ConcurrencyFailure");

        using var connection = database.OpenReadback();
        Assert.NotEqual(oldUserStamp, user.ConcurrencyStamp);
        Assert.NotEqual(oldRoleStamp, role.ConcurrencyStamp);
        Assert.Equal(user.ConcurrencyStamp, Scalar(connection,
            """SELECT "ConcurrencyStamp" FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        Assert.Equal(role.ConcurrencyStamp, Scalar(connection,
            """SELECT "ConcurrencyStamp" FROM "AspNetRoles" WHERE "Id" = :p0""", role.Id));
        Assert.Equal(winnerEmail, Scalar(connection, """SELECT "Email" FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        Assert.Equal(winnerRole, Scalar(connection, """SELECT "Name" FROM "AspNetRoles" WHERE "Id" = :p0""", role.Id));

        await using var retry = database.NewScope();
        var retryUsers = retry.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var retryRoles = retry.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var recoveredUser = (await retryUsers.FindByIdAsync(user.Id))!;
        var recoveredRole = (await retryRoles.FindByIdAsync(role.Id))!;
        var retryEmail = Unique("Retry") + "@example.test";
        var retryRole = Unique("RetryRole");
        Succeeded(await retryUsers.SetEmailAsync(recoveredUser, retryEmail));
        Succeeded(await retryRoles.SetRoleNameAsync(recoveredRole, retryRole));
        Succeeded(await retryRoles.UpdateAsync(recoveredRole));
        Assert.Equal(retryEmail, Scalar(connection, """SELECT "Email" FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        Assert.Equal(retryRole, Scalar(connection, """SELECT "Name" FROM "AspNetRoles" WHERE "Id" = :p0""", role.Id));
    }

    [Fact]
    public async Task ManagerWritesShareExplicitTransactionAndRollbackAtomically()
    {
        await using var writer = database.NewScope();
        var context = writer.ServiceProvider.GetRequiredService<IdentityContext>();
        var users = writer.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = writer.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = NewUser();
        var role = new IdentityRole(Unique("Role"));

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Succeeded(await users.CreateAsync(user));
            Succeeded(await roles.CreateAsync(role));
            Succeeded(await users.AddToRoleAsync(user, role.Name!));
            Succeeded(await users.AddClaimAsync(user, new Claim("operation", "rollback")));
            Assert.Equal(1, Count(context.Database.GetDbConnection(),
                """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId" = :p0""",
                transaction.GetDbTransaction(), user.Id));
            await transaction.RollbackAsync();
        }

        using var connection = database.OpenReadback();
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetRoles" WHERE "Id" = :p0""", role.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId" = :p0""", user.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserClaims" WHERE "UserId" = :p0""", user.Id));

        await using var retry = database.NewScope();
        var retryUsers = retry.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Succeeded(await retryUsers.CreateAsync(new IdentityUser { Id = user.Id, UserName = user.UserName, Email = user.Email }));
        Assert.Equal(1, Count(connection, """SELECT COUNT(*) FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
    }

    [Fact]
    public async Task UntrackedDependentsCascadeOnManagerDeletes()
    {
        await using var writer = database.NewScope();
        var users = writer.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roles = writer.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = NewUser();
        var firstRole = new IdentityRole(Unique("Role"));
        var secondRole = new IdentityRole(Unique("Role"));
        Succeeded(await users.CreateAsync(user));
        Succeeded(await roles.CreateAsync(firstRole));
        Succeeded(await roles.CreateAsync(secondRole));
        Succeeded(await users.AddToRolesAsync(user, [firstRole.Name!, secondRole.Name!]));
        Succeeded(await users.AddClaimAsync(user, new Claim("kind", "user")));
        Succeeded(await roles.AddClaimAsync(firstRole, new Claim("kind", "role")));
        Succeeded(await users.AddLoginAsync(user, new UserLoginInfo("oidc", Unique("Key"), "OIDC")));
        Succeeded(await users.SetAuthenticationTokenAsync(user, "oidc", "refresh", "value"));

        await using var remover = database.NewScope();
        var removerRoles = remover.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var removerUsers = remover.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var foundRole = (await removerRoles.FindByIdAsync(firstRole.Id))!;
        var foundUser = (await removerUsers.FindByIdAsync(user.Id))!;

        using var connection = database.OpenReadback();
        Assert.Equal(1, Count(connection, """SELECT COUNT(*) FROM "AspNetRoleClaims" WHERE "RoleId" = :p0""", firstRole.Id));
        Assert.Equal(2, Count(connection, """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId" = :p0""", user.Id));
        Succeeded(await removerRoles.DeleteAsync(foundRole));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetRoles" WHERE "Id" = :p0""", firstRole.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetRoleClaims" WHERE "RoleId" = :p0""", firstRole.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "RoleId" = :p0""", firstRole.Id));
        Assert.Equal(1, Count(connection, """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId" = :p0""", user.Id));

        Succeeded(await removerUsers.DeleteAsync(foundUser));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUsers" WHERE "Id" = :p0""", user.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserClaims" WHERE "UserId" = :p0""", user.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserLogins" WHERE "UserId" = :p0""", user.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserTokens" WHERE "UserId" = :p0""", user.Id));
        Assert.Equal(0, Count(connection, """SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId" = :p0""", user.Id));
        Assert.Equal(1, Count(connection, """SELECT COUNT(*) FROM "AspNetRoles" WHERE "Id" = :p0""", secondRole.Id));
    }

    private static IdentityUser NewUser(int userNameLength = 0)
    {
        var name = Unique("User");
        return new IdentityUser { UserName = name.PadRight(userNameLength, 'u'), Email = name + "@example.test" };
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N");

    private static void Succeeded(IdentityResult result) =>
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));

    private static void AssertError(IdentityResult result, string code) =>
        Assert.Equal(code, Assert.Single(result.Errors).Code);

    private static void AssertUniqueViolation(DbConnection connection, string sql, params string[] values)
    {
        using var command = Command(connection, sql, null, values);
        var error = Assert.Throws<DmException>(() => command.ExecuteNonQuery());
        Assert.Equal(UniqueViolation, error.Number);
    }

    private static int Count(DbConnection connection, string sql, params string[] values) =>
        Convert.ToInt32(Scalar(connection, sql, values), CultureInfo.InvariantCulture);

    private static int Count(DbConnection connection, string sql, DbTransaction transaction, params string[] values)
    {
        using var command = Command(connection, sql, transaction, values);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static object? Scalar(DbConnection connection, string sql, params string[] values)
    {
        using var command = Command(connection, sql, null, values);
        return command.ExecuteScalar();
    }

    private static DbCommand Command(DbConnection connection, string sql, DbTransaction? transaction, string[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{index}";
            parameter.Value = values[index];
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
