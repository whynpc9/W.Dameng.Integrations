using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TickerQ.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces;
using TickerQ.Utilities.Interfaces.Managers;
using TickerQ.Utilities.Models;
using Xunit;

namespace W.TickerQ.Dameng.Tests;

public sealed class OperationalStoreTests
{
    private const string Function = "DamengAcceptance";
    private const string Cron = "0 0 0 1 1 *";
    private static readonly DateTime Epoch = new DateTime(2030, 4, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1);
    private static readonly string LongRequest = new('q', 96_000);
    private static readonly byte[] RequestBytes = JsonSerializer.SerializeToUtf8Bytes(LongRequest);
    private static readonly string LongException = string.Concat(Enumerable.Repeat("达梦失败-🧪-", 6_000)) + "-end";

    static OperationalStoreTests()
    {
        RegisterJob();
        TickerFunctionProvider.Build();
    }

    private static void RegisterJob()
        => TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, FailJobAsync)
        });

    [Fact]
    public Task Migration_applies_in_the_owned_tablespace_with_the_native_unique_index()
        => LiveDatabase.RunAsync(async database =>
        {
            using var node = database.CreateNode("migration");
            await using var context = await ContextAsync(node);
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Single(await context.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            var tablespaces = await database.QueryAsync("""
                SELECT TABLESPACE_NAME FROM USER_TABLES
                WHERE TABLE_NAME IN ('TimeTickers', 'CronTickers', 'CronTickerOccurrences')
                """, async reader =>
            {
                var values = new List<string>();
                while (await reader.ReadAsync())
                    values.Add(reader.GetString(0));
                return values;
            });
            Assert.Equal(3, tablespaces.Count);
            Assert.True(tablespaces.All(name => name == database.Tablespace), "TickerQ table placement differs from its isolated tablespace.");
            Assert.Equal(1, await database.CountAsync("""
                SELECT COUNT(*) FROM USER_INDEXES
                WHERE INDEX_NAME = 'UQ_CronTickerId_ExecutionTime'
                  AND TABLE_NAME = 'CronTickerOccurrences' AND UNIQUENESS = 'UNIQUE'
                """));
        });

    [Fact]
    public Task Native_managers_round_trip_retry_arrays_and_long_request_bytes()
        => LiveDatabase.RunAsync(async database =>
        {
            using var first = database.CreateNode("write", new FixedClock(Epoch));
            using var second = database.CreateNode("read");
            var provider = Store(second);
            foreach (var intervals in new int[]?[] { null, [], [1, 5, 60, 60] })
            {
                var time = await AddTimeAsync(first, Epoch.AddMinutes(1), intervals);
                var cron = await AddCronAsync(first, intervals);
                var timeReadback = await provider.GetTimeTickerById(time.Id);
                var cronReadback = await provider.GetCronTickerById(cron.Id, default);
                Assert.Equal(intervals, timeReadback.RetryIntervals);
                Assert.Equal(intervals, cronReadback.RetryIntervals);
                Assert.Equal(RequestBytes, await provider.GetTimeTickerRequest(time.Id, default));
                Assert.Equal(RequestBytes, cronReadback.Request);
                await AssertRawRequestAndIntervalsAsync(database, "TimeTickers", time.Id, intervals);
                await AssertRawRequestAndIntervalsAsync(database, "CronTickers", cron.Id, intervals);
            }

            // A tracked element edit must be detected without replacing the array instance.
            var tracked = await AddTimeAsync(first, Epoch.AddMinutes(1), [3, 6]);
            await using (var context = await ContextAsync(second))
            {
                var entity = await context.Set<TimeTickerEntity>().SingleAsync(row => row.Id == tracked.Id);
                entity.RetryIntervals[0] = 9;
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await AssertRawRequestAndIntervalsAsync(database, "TimeTickers", tracked.Id, [9, 6]);
        });

    [Fact]
    public Task Two_nodes_queue_only_one_occurrence_and_NoUpdate_does_not_claim_the_existing_row()
        => LiveDatabase.RunAsync(async database =>
        {
            using var first = database.CreateNode("cron-a", new FixedClock(Epoch));
            using var second = database.CreateNode("cron-b", new FixedClock(Epoch.AddTicks(1)));
            var cron = await AddCronAsync(first, [3, 8]);
            var time = Epoch.AddSeconds(1);
            var (left, right) = await RaceAsync(() => QueueCronAsync(first, cron, time), () => QueueCronAsync(second, cron, time));
            var winner = Assert.Single(left.Concat(right));
            Assert.Equal(left.Count == 1 ? "cron-a" : "cron-b", winner.LockHolder);
            Assert.Empty(await QueueCronAsync(first, cron, time));
            Assert.Empty(await QueueCronAsync(second, cron, time));
            Assert.Equal(1, await database.CountAsync("SELECT COUNT(*) FROM \"CronTickerOccurrences\""));

            var stored = await ReadJobAsync(database, "CronTickerOccurrences", winner.Id);
            Assert.Equal(winner.LockHolder, stored.Owner);
            Assert.Equal(winner.UpdatedAt.Ticks, stored.UpdatedAt.Ticks);
            Assert.Equal(TickerStatus.Queued, stored.Status);
            Assert.Equal(RequestBytes, await Store(second).GetCronTickerOccurrenceRequest(winner.Id));
            var adjacent = Assert.Single(await QueueCronAsync(second, cron, time.AddTicks(1)));
            Assert.NotEqual(winner.Id, adjacent.Id);
            Assert.Equal(2, await database.CountAsync("SELECT COUNT(*) FROM \"CronTickerOccurrences\""));

            // The native provider treats zero as a lost claim. Verify the runner's exact count as well.
            await using var context = await ContextAsync(first);
            var occurrences = context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>();
            var direct = new CronTickerOccurrenceEntity<CronTickerEntity>
            {
                Id = Guid.NewGuid(),
                CronTickerId = cron.Id,
                ExecutionTime = time.AddTicks(2),
                CreatedAt = Epoch,
                UpdatedAt = Epoch
            };
            Assert.Equal(1, await occurrences.Upsert(direct)
                .On(x => new { x.ExecutionTime, x.CronTickerId }).NoUpdate().RunAsync());
            direct.Id = Guid.NewGuid();
            Assert.Equal(0, await occurrences.Upsert(direct)
                .On(x => new { x.ExecutionTime, x.CronTickerId }).NoUpdate().RunAsync());
            Assert.Equal(3, await database.CountAsync("SELECT COUNT(*) FROM \"CronTickerOccurrences\""));
        });

    [Fact]
    public Task Competing_time_claims_reject_a_stale_UpdatedAt_even_one_tick_apart()
        => LiveDatabase.RunAsync(async database =>
        {
            using var setup = database.CreateNode("claim-setup", new FixedClock(Epoch));
            using var first = database.CreateNode("claim-a", new FixedClock(Epoch.AddTicks(1)));
            using var second = database.CreateNode("claim-b", new FixedClock(Epoch.AddTicks(2)));
            var ticker = await AddTimeAsync(setup, Epoch.AddSeconds(1));
            var snapshotA = Assert.Single(await Store(first).GetEarliestTimeTickers());
            var snapshotB = Assert.Single(await Store(second).GetEarliestTimeTickers());
            Assert.Equal(ticker.Id, snapshotA.Id);
            Assert.Equal(ticker.Id, snapshotB.Id);
            Assert.Equal(Epoch.Ticks, snapshotA.UpdatedAt.Ticks);
            Assert.Equal(Epoch.Ticks, snapshotB.UpdatedAt.Ticks);
            var (left, right) = await RaceAsync(
                () => CollectAsync(Store(first).QueueTimeTickers([snapshotA])),
                () => CollectAsync(Store(second).QueueTimeTickers([snapshotB])));
            var winner = Assert.Single(left.Concat(right));
            var stored = await ReadJobAsync(database, "TimeTickers", ticker.Id);
            Assert.Equal(winner.LockHolder, stored.Owner);
            Assert.Equal(winner.UpdatedAt.Ticks, stored.UpdatedAt.Ticks);
            Assert.InRange(stored.UpdatedAt.Ticks - Epoch.Ticks, 1, 2);
            Assert.Equal(TickerStatus.Queued, stored.Status);
        });

    [Fact]
    public Task Restart_recovers_its_time_and_cron_jobs_and_persists_both_failures_after_retry()
        => LiveDatabase.RunAsync(async database =>
        {
            const string stableNode = "recovery";
            using var other = database.CreateNode("healthy");
            var due = DateTime.UtcNow.AddMinutes(-1);
            Guid timeId, occurrenceId, foreignId;
            using (var beforeRestart = database.CreateNode(stableNode))
            {
                var time = await AddTimeAsync(beforeRestart, due, [0], retries: 1);
                timeId = time.Id;
                Assert.Single(await Store(beforeRestart).AcquireImmediateTimeTickersAsync([timeId]));

                var cron = await AddCronAsync(beforeRestart, [0], retries: 1);
                occurrenceId = Assert.Single(await QueueCronAsync(beforeRestart, cron, due)).Id;
                Assert.Single(await Store(beforeRestart).AcquireImmediateCronOccurrencesAsync([occurrenceId]));

                var foreign = await AddTimeAsync(other, due);
                foreignId = foreign.Id;
                Assert.Single(await Store(other).AcquireImmediateTimeTickersAsync([foreignId]));
            }

            Assert.Equal(TickerStatus.InProgress, (await ReadJobAsync(database, "TimeTickers", timeId)).Status);
            Assert.Equal(TickerStatus.InProgress, (await ReadJobAsync(database, "CronTickerOccurrences", occurrenceId)).Status);

            using var afterRestart = database.CreateNode(stableNode, runScheduler: true);
            // The test restarts a host in-process. A real process restart registers functions afresh.
            RegisterJob();
            afterRestart.UseTickerQ();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await afterRestart.StartAsync(deadline.Token);
            try
            {
                await AssertFailedAsync(database, "TimeTickers", timeId, deadline.Token);
                await AssertFailedAsync(database, "CronTickerOccurrences", occurrenceId, deadline.Token);
                var attempts = afterRestart.Services.GetRequiredService<ExecutionProbe>().Attempts;
                Assert.Equal(2, attempts[timeId]);
                Assert.Equal(2, attempts[occurrenceId]);
                Assert.False(attempts.ContainsKey(foreignId));
                var foreign = await ReadJobAsync(database, "TimeTickers", foreignId);
                Assert.Equal(TickerStatus.InProgress, foreign.Status);
                Assert.Equal("healthy", foreign.Owner);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await afterRestart.StopAsync(stop.Token);
            }
        });

    private static async Task FailJobAsync(CancellationToken token, IServiceProvider services, TickerFunctionContext context)
    {
        var request = await TickerRequestProvider.GetRequestAsync<string>(context, token);
        if (request != LongRequest)
            throw new InvalidOperationException("The stored request did not reach the TickerQ handler intact.");

        services.GetRequiredService<ExecutionProbe>().Attempts.AddOrUpdate(context.Id, 1, (_, count) => count + 1);
        throw new InvalidOperationException(LongException);
    }

    private static async Task AssertFailedAsync(LiveDatabase database, string table, Guid id, CancellationToken token)
    {
        var row = await ReadJobAsync(database, table, id);
        while (row.Status != TickerStatus.Failed)
        {
            await Task.Delay(100, token);
            row = await ReadJobAsync(database, table, id);
        }

        Assert.Equal(1, row.RetryCount);
        Assert.NotNull(row.ExecutedAt);
        using var exception = JsonDocument.Parse(Assert.IsType<string>(row.Exception));
        var message = exception.RootElement.GetProperty("Message").GetString();
        Assert.True(message == LongException,
            $"Unexpected persisted exception for {table} (message length {message?.Length}, expected {LongException.Length}).");
    }

    private static Task<JobRow> ReadJobAsync(LiveDatabase database, string table, Guid id)
        => database.QueryAsync($"""
            SELECT "Status", "LockHolder", "UpdatedAt", "RetryCount", "ExecutedAt", "ExceptionMessage"
            FROM "{table}" WHERE "Id" = :id
            """, async reader =>
        {
            Assert.True(await reader.ReadAsync(), "Expected job was not found by independent SQL.");
            var row = new JobRow((TickerStatus)reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetDateTime(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
            Assert.False(await reader.ReadAsync());
            return row;
        }, id);

    private static Task<bool> AssertRawRequestAndIntervalsAsync(LiveDatabase database, string table, Guid id, int[]? intervals)
        => database.QueryAsync($"""
            SELECT "Request", "RetryIntervals" FROM "{table}" WHERE "Id" = :id
            """, async reader =>
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(RequestBytes, reader.GetFieldValue<byte[]>(0));
            Assert.Equal(intervals is null ? null : JsonSerializer.Serialize(intervals),
                reader.IsDBNull(1) ? null : reader.GetString(1));
            Assert.False(await reader.ReadAsync());
            return true;
        }, id);

    private static async Task<TimeTickerEntity> AddTimeAsync(IHost node, DateTime executionTime, int[]? intervals = null, int retries = 0)
    {
        var manager = node.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        return RequireSuccess(await manager.AddAsync(new TimeTickerEntity
        {
            Function = Function,
            ExecutionTime = executionTime,
            Retries = retries,
            RetryIntervals = intervals!,
            Request = RequestBytes
        }));
    }

    private static async Task<CronTickerEntity> AddCronAsync(IHost node, int[]? intervals, int retries = 0)
    {
        var manager = node.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
        return RequireSuccess(await manager.AddAsync(new CronTickerEntity
        {
            Function = Function,
            Expression = Cron,
            Retries = retries,
            RetryIntervals = intervals!,
            Request = RequestBytes
        }));
    }

    private static T RequireSuccess<T>(TickerResult<T> result) where T : class
    {
        if (!result.IsSucceeded)
            throw new InvalidOperationException("Native TickerQ manager failed.", result.Exception);
        return result.Result;
    }

    private static Task<List<CronTickerOccurrenceEntity<CronTickerEntity>>> QueueCronAsync(IHost node, CronTickerEntity cron, DateTime time)
        => CollectAsync(Store(node).QueueCronTickerOccurrences((time, [new InternalManagerContext(cron.Id)
        {
            FunctionName = cron.Function,
            Expression = cron.Expression,
            Retries = cron.Retries,
            RetryIntervals = cron.RetryIntervals
        }])));

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store(IHost node)
        => node.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    private static Task<DamengTickerQDbContext> ContextAsync(IHost node)
        => node.Services.GetRequiredService<IDbContextFactory<DamengTickerQDbContext>>().CreateDbContextAsync();

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> items)
    {
        var results = new List<T>();
        await foreach (var item in items)
            results.Add(item);
        return results;
    }

    private static async Task<(T Left, T Right)> RaceAsync<T>(Func<Task<T>> left, Func<Task<T>> right)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftTask = RunAsync(left);
        var rightTask = RunAsync(right);
        start.SetResult();
        await Task.WhenAll(leftTask, rightTask);
        return (await leftTask, await rightTask);

        async Task<T> RunAsync(Func<Task<T>> work)
        {
            await start.Task;
            return await work();
        }
    }

    private sealed record FixedClock(DateTime UtcNow) : ITickerClock;
    private sealed record JobRow(TickerStatus Status, string? Owner, DateTime UpdatedAt, int RetryCount, DateTime? ExecutedAt, string? Exception);
}
