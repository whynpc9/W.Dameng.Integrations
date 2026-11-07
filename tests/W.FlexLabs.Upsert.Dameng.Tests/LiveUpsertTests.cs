using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.Configurations;
using TickerQ.EntityFrameworkCore.Customizer;
using TickerQ.EntityFrameworkCore.DependencyInjection;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces;
using TickerQ.Utilities.Models;
using W.Dm;
using Xunit;

namespace W.FlexLabs.Upsert.Dameng.Tests;

public sealed class LiveUpsertTests : IClassFixture<LiveDatabase>
{
    private static readonly DateTime Timestamp = new(2026, 11, 7, 8, 23, 45, DateTimeKind.Unspecified);
    private readonly LiveDatabase _database;

    public LiveUpsertTests(LiveDatabase database) => _database = database;

    [Fact]
    public void Sync_no_update_reports_one_then_zero()
    {
        using var context = _database.CreateContext();
        Assert.True(context.Database.ProviderName == "W.EntityFrameworkCore.Dameng.WDriver");

        Assert.Equal(1, context.Rows.Upsert(NewRow(1, 1, 9)).NoUpdate().Run());
        Assert.Equal(0, context.Rows.Upsert(NewRow(1, 1, 99)).NoUpdate().Run());

        using var reader = _database.CreateContext();
        Assert.Equal(9, reader.Rows.Single(row => row.Bucket == 1).Counter);
    }

    [Fact]
    public async Task Async_composite_nullable_match_and_parameter_conversion()
    {
        await using var context = _database.CreateContext();
        var first = NewRow(2, 1, 6);
        first.Payload = "测试 '); -- ?";
        first.LargePayload = new string('字', 12000);
        first.Stamp = Timestamp.AddTicks(1234560);
        first.State = ItemState.Ready;
        Assert.Equal(1, await context.Rows.Upsert(first).On(row => new { row.Bucket, row.Part }).RunAsync());

        var matching = NewRow(2, 2, 12);
        matching.State = ItemState.Done;
        Assert.Equal(1, await context.Rows.Upsert(matching).On(row => new { row.Bucket, row.Part })
            .WhenMatched((existing, incoming) => new Item
            {
                State = incoming.State,
                Counter = existing.Counter + incoming.Counter
            }).RunAsync());

        var distinct = NewRow(2, 3, 7);
        distinct.Part = 5;
        Assert.Equal(1, await context.Rows.Upsert(distinct).On(row => new { row.Bucket, row.Part }).RunAsync());

        await using var reader = _database.CreateContext();
        var rows = await reader.Rows.Where(row => row.Bucket == 2).OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Null(rows[0].Part);
        Assert.Equal(18, rows[0].Counter);
        Assert.Equal(ItemState.Done, rows[0].State);
        Assert.Equal(first.Payload, rows[0].Payload);
        Assert.Equal(first.LargePayload, rows[0].LargePayload);
        Assert.Equal(first.Stamp, rows[0].Stamp);
        Assert.Equal(5, rows[1].Part);
        Assert.Equal("server default", rows[1].DefaultText);
        Assert.Null(rows[1].Payload);
        Assert.Null(rows[1].LargePayload);
        Assert.Equal("DONE", _database.Scalar("SELECT \"State\" FROM " + _database.Table + " WHERE \"Id\" = 201"));
    }

    [Fact]
    public async Task Update_condition_reports_zero_when_false_and_honors_null_check()
    {
        await using var context = _database.CreateContext();
        Assert.Equal(1, await context.Rows.Upsert(NewRow(3, 1, 1)).RunAsync());
        var incoming = NewRow(3, 1, 7);
        Assert.Equal(0, await context.Rows.Upsert(incoming)
            .UpdateIf(existing => existing.Payload != null)
            .WhenMatched((existing, source) => new Item { Counter = source.Counter }).RunAsync());
        Assert.Equal(1, await context.Rows.Upsert(incoming)
            .UpdateIf(existing => existing.Payload == null)
            .WhenMatched((existing, source) => new Item { Counter = source.Counter }).RunAsync());
        await using var reader = _database.CreateContext();
        Assert.Equal(7, (await reader.Rows.SingleAsync(row => row.Bucket == 3)).Counter);
    }

    [Fact]
    public async Task Batch_uses_input_order_and_actual_counts()
    {
        await using var context = _database.CreateContext();
        var first = NewRow(4, 1, 2);
        var second = NewRow(4, 2, 3);
        second.Part = 2;
        var duplicate = NewRow(4, 1, 100);
        Assert.Equal(2, await context.Rows.UpsertRange(first, second, duplicate).NoUpdate().RunAsync());
        Assert.Equal(2, await context.Rows.UpsertRange(first, duplicate).RunAsync());

        await using var reader = _database.CreateContext();
        var counters = await reader.Rows.Where(row => row.Bucket == 4).OrderBy(row => row.Id)
            .Select(row => row.Counter).ToArrayAsync();
        Assert.Equal(new[] { 100, 3 }, counters);
    }

    [Fact]
    public async Task Nonmatch_unique_violation_propagates_and_rolls_back_batch()
    {
        await using var context = _database.CreateContext();
        var existing = NewRow(5, 1, 1);
        Assert.Equal(1, await context.Rows.Upsert(existing).RunAsync());
        var wouldInsert = NewRow(5, 2, 1);
        wouldInsert.Part = 2;
        var conflicts = NewRow(5, 3, 1);
        conflicts.Part = 3;
        conflicts.UniqueCode = existing.UniqueCode;

        await Assert.ThrowsAsync<DmException>(
            () => context.Rows.UpsertRange(wouldInsert, conflicts).NoUpdate().RunAsync());

        await using var reader = _database.CreateContext();
        Assert.Equal(new[] { existing.Id }, await reader.Rows.Where(row => row.Bucket == 5)
            .Select(row => row.Id).ToArrayAsync());
        Assert.Equal(1, await context.Rows.Upsert(wouldInsert).RunAsync());
        Assert.Equal(2, await reader.Rows.CountAsync(row => row.Bucket == 5));
    }

    [Fact]
    public async Task Existing_transaction_failure_preserves_prior_work_and_can_commit()
    {
        await using var context = _database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var existing = NewRow(6, 1, 1);
        context.Rows.Add(existing);
        await context.SaveChangesAsync();
        var wouldInsert = NewRow(6, 2, 1);
        wouldInsert.Part = 2;
        var conflicts = NewRow(6, 3, 1);
        conflicts.Part = 3;
        conflicts.UniqueCode = existing.UniqueCode;

        await Assert.ThrowsAsync<DmException>(
            () => context.Rows.UpsertRange(wouldInsert, conflicts).NoUpdate().RunAsync());
        var valid = NewRow(6, 4, 1);
        valid.Part = 4;
        Assert.Equal(1, await context.Rows.Upsert(valid).NoUpdate().RunAsync());

        await transaction.CommitAsync();
        await using var reader = _database.CreateContext();
        Assert.Equal(new[] { existing.Id, valid.Id }, await reader.Rows.Where(row => row.Bucket == 6)
            .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync());
    }

    [Fact]
    public void Existing_sync_transaction_rollback_removes_upsert()
    {
        using var context = _database.CreateContext();
        using var transaction = context.Database.BeginTransaction(IsolationLevel.ReadCommitted);
        Assert.Equal(1, context.Rows.Upsert(NewRow(7, 1, 1)).NoUpdate().Run());
        transaction.Rollback();

        using var reader = _database.CreateContext();
        Assert.False(reader.Rows.Any(row => row.Bucket == 7));
    }

    [Fact]
    public async Task Independent_connections_contend_with_exactly_one_insert_winner()
    {
        const int contenders = 6;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int openConnections = 0;
        var attempts = Enumerable.Range(1, contenders).Select(async attempt =>
        {
            await using var context = _database.CreateContext();
            await context.Database.OpenConnectionAsync(deadline.Token);
            if (Interlocked.Increment(ref openConnections) == contenders)
                ready.TrySetResult();
            await ready.Task.WaitAsync(deadline.Token);
            var affected = await context.Rows.Upsert(NewRow(8, 1, attempt)).NoUpdate().RunAsync(deadline.Token);
            return (Attempt: attempt, Affected: affected);
        }).ToArray();

        var counts = await Task.WhenAll(attempts);
        Assert.Equal(contenders - 1, counts.Count(count => count.Affected == 0));
        var winner = Assert.Single(counts, count => count.Affected == 1);
        await using var reader = _database.CreateContext();
        var persisted = await reader.Rows.SingleAsync(row => row.Bucket == 8);
        Assert.Equal(winner.Attempt, persisted.Counter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_upsert_timeout_or_cancellation_leaves_no_partial_write(bool cancel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var holder = _database.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, deadline.Token);
        var held = NewRow(cancel ? 11 : 10, 1, 7);
        Assert.Equal(1, await holder.Rows.Upsert(held).NoUpdate().RunAsync(deadline.Token));

        var observer = new LockObserver();
        await using var contender = _database.CreateContext(observer);
        contender.Database.SetCommandTimeout(cancel ? 30 : 1);
        var competing = NewRow(held.Bucket, 2, 9);
        competing.Part = 2;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pending = contender.Rows.Upsert(competing).NoUpdate().RunAsync(request.Token);
        await observer.LockStarted.Task.WaitAsync(deadline.Token);
        if (cancel)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), deadline.Token);
            Assert.False(pending.IsCompleted);
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        else
        {
            await Assert.ThrowsAsync<DmTimeoutException>(async () => await pending);
        }

        Assert.Null(contender.Database.CurrentTransaction);
        await holderTransaction.CommitAsync(deadline.Token);
        await using var reader = _database.CreateContext();
        Assert.Equal(held.Id, (await reader.Rows.SingleAsync(row => row.Bucket == held.Bucket, deadline.Token)).Id);
        contender.Database.SetCommandTimeout(30);
        Assert.Equal(1, await contender.Rows.Upsert(competing).NoUpdate().RunAsync(deadline.Token));
        Assert.Equal(2, await reader.Rows.CountAsync(row => row.Bucket == held.Bucket, deadline.Token));
    }

    private sealed class LockObserver : DbCommandInterceptor
    {
        public TaskCompletionSource LockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("LOCK TABLE ", StringComparison.Ordinal))
                LockStarted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Returning_is_rejected_without_writing()
    {
        await using var context = _database.CreateContext();
        Assert.Throws<NotSupportedException>(() => context.Rows.Upsert(NewRow(9, 1, 1)).RunAndReturn());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context.Rows.Upsert(NewRow(9, 1, 1)).RunAndReturnAsync());
        await using var reader = _database.CreateContext();
        Assert.False(await reader.Rows.AnyAsync(row => row.Bucket == 9));
    }

    [Fact]
    public async Task TickerQ_persistence_queues_one_cron_occurrence_per_execution_key()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<LiveContext>>(new LiveContextFactory(_database));
        services.AddTickerQ(options =>
        {
            options.DisableBackgroundServices();
            options.AddOperationalStore(store =>
                store.UseApplicationDbContext<LiveContext>(ConfigurationType.IgnoreModelCustomizer));
        });
        await using var provider = services.BuildServiceProvider();
        var persistence = provider.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var item = new InternalManagerContext(Guid.NewGuid()) { FunctionName = "probe", Expression = "* * * * *" };
        var argument = (Timestamp, new[] { item });

        var first = new List<CronTickerOccurrenceEntity<CronTickerEntity>>();
        await foreach (var occurrence in persistence.QueueCronTickerOccurrences(argument))
            first.Add(occurrence);
        await foreach (var _ in persistence.QueueCronTickerOccurrences(argument))
            Assert.Fail("TickerQ yielded a second occurrence for the same execution key.");

        await using var reader = _database.CreateContext();
        var stored = await reader.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().SingleAsync();
        Assert.Equal(Assert.Single(first).Id, stored.Id);
        Assert.Equal(item.Id, stored.CronTickerId);
        Assert.Equal(Timestamp, stored.ExecutionTime);
        Assert.Equal(TickerStatus.Queued, stored.Status);
    }

    private static Item NewRow(int bucket, int item, int counter) => new()
    {
        Id = 100 * bucket + item,
        Bucket = bucket,
        Counter = counter,
        UniqueCode = $"{bucket}-{item}",
        Stamp = Timestamp
    };
}

public sealed class LiveDatabase : IDisposable
{
    private readonly DbContextOptions<LiveContext> _options;
    private readonly string _connection;
    private readonly string _tableName = "FLEX_PROBE_" + Guid.NewGuid().ToString("N").ToUpperInvariant();

    private string TickerTableName => _tableName + "_TQ";

    public LiveDatabase()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("FlexLabs live tests require Unix file permissions.");
            var path = Environment.GetEnvironmentVariable("FLEXLABS_DM_ISOLATION")
                ?? throw new InvalidOperationException("Run the FlexLabs provision and test script.");
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                throw new InvalidOperationException("Isolation state must have mode 0600.");
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            var root = state.RootElement;
            var user = root.GetProperty("User").GetString()!;
            var tablespace = root.GetProperty("Tablespace").GetString()!;
            if (!root.GetProperty("Ready").GetBoolean() || !user.StartsWith("FLEX_U_", StringComparison.Ordinal)
                || !tablespace.StartsWith("FLEX_TS_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("FlexLabs isolation is not ready.");
            }

            _connection = root.GetProperty("ConnectionString").GetString()!;
            Table = $"\"{_tableName}\"";
            Assert.True(Equals(Scalar("SELECT USER FROM DUAL"), user), "Incorrect session user.");
            Assert.True(Equals(Scalar("SELECT DEFAULT_TABLESPACE FROM USER_USERS"), tablespace), "Incorrect tablespace.");
            _options = new DbContextOptionsBuilder<LiveContext>()
                .UseDamengWithWDriver(_connection)
                .UseDamengUpsert().Options;
            Execute($"""
                CREATE TABLE {Table} (
                    "Id" INT PRIMARY KEY,
                    "Bucket" INT NOT NULL,
                    "Part" INT NULL,
                    "Counter" INT NOT NULL,
                    "UniqueCode" VARCHAR(80) NOT NULL UNIQUE,
                    "State" VARCHAR(8) NOT NULL,
                    "Stamp" TIMESTAMP(6) NOT NULL,
                    "Payload" VARCHAR(100) NULL,
                    "LargePayload" CLOB NULL,
                    "DefaultText" VARCHAR(80) DEFAULT 'server default',
                    UNIQUE ("Bucket", "Part")
                )
                """);
            Execute($"""
                CREATE TABLE "{TickerTableName}" (
                    "Id" CHAR(36) PRIMARY KEY,
                    "Status" INT NOT NULL,
                    "LockHolder" CLOB NULL,
                    "ExecutionTime" TIMESTAMP(7) NOT NULL,
                    "CronTickerId" CHAR(36) NOT NULL,
                    "LockedAt" TIMESTAMP(7) NULL,
                    "ExecutedAt" TIMESTAMP(7) NULL,
                    "ExceptionMessage" CLOB NULL,
                    "SkippedReason" CLOB NULL,
                    "ElapsedTime" BIGINT NOT NULL,
                    "RetryCount" INT NOT NULL,
                    "CreatedAt" TIMESTAMP(7) NOT NULL,
                    "UpdatedAt" TIMESTAMP(7) NOT NULL,
                    UNIQUE ("CronTickerId", "ExecutionTime")
                )
                """);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"FlexLabs live fixture initialization failed ({error.GetType().Name}, 0x{error.HResult:X8}).");
        }
    }

    public string Table { get; }
    internal LiveContext CreateContext(DbCommandInterceptor? observer = null)
        => new(observer is null ? _options : new DbContextOptionsBuilder<LiveContext>(_options)
            .AddInterceptors(observer).Options, _tableName, TickerTableName);

    internal object? Scalar(string sql)
    {
        using var connection = new DmConnection(_connection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var connection = new DmConnection(_connection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            Execute($"DROP TABLE \"{TickerTableName}\"");
            Execute("DROP TABLE " + Table);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"FlexLabs test table cleanup failed ({error.GetType().Name}, 0x{error.HResult:X8}).");
        }
    }
}

internal sealed class LiveContextFactory(LiveDatabase database) : IDbContextFactory<LiveContext>
{
    public LiveContext CreateDbContext() => database.CreateContext();
}

internal sealed class LiveContext(DbContextOptions<LiveContext> options, string tableName, string tickerTableName)
    : DbContext(options)
{
    public DbSet<Item> Rows => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Item>().ToTable(tableName);
        entity.Property(row => row.Id).ValueGeneratedNever();
        entity.Property(row => row.UniqueCode).HasMaxLength(80);
        entity.Property(row => row.State).HasMaxLength(8).HasConversion(
            value => value == ItemState.Ready ? "READY" : "DONE",
            value => value == "READY" ? ItemState.Ready : ItemState.Done);
        entity.Property(row => row.Stamp).HasColumnType("TIMESTAMP(6)");
        entity.Property(row => row.Payload).HasMaxLength(100);
        entity.Property(row => row.LargePayload).HasColumnType("CLOB");
        entity.Property(row => row.DefaultText).HasMaxLength(80).HasDefaultValueSql("'server default'");

        var ticker = modelBuilder.Entity<CronTickerOccurrenceEntity<CronTickerEntity>>();
        new CronTickerOccurrenceConfigurations<CronTickerEntity>().Configure(ticker);
        ticker.ToTable(tickerTableName, schema: null);
        // This fixture exercises the occurrence queue without the scheduler's parent tables.
        ticker.Ignore(row => row.CronTicker);
        modelBuilder.Ignore<CronTickerEntity>();
    }
}

internal sealed class Item
{
    public int Id { get; set; }
    public int Bucket { get; set; }
    public int? Part { get; set; }
    public int Counter { get; set; }
    public string UniqueCode { get; set; } = "";
    public ItemState State { get; set; }
    public DateTime Stamp { get; set; }
    public string? Payload { get; set; }
    public string? LargePayload { get; set; }
    public string? DefaultText { get; set; }
}

internal enum ItemState { Ready, Done }
