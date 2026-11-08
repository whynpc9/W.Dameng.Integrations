#if TICKERQ_F14
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TickerQ.DependencyInjection;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces;
using TickerQ.Utilities.Interfaces.Managers;
using TickerQ.Utilities.Models;
using Xunit;

namespace W.TickerQ.Dameng.Tests;

public sealed partial class OperationalStoreTests
{
    [Fact]
    public Task Timeout_takeover_fences_previous_holder_and_runs_only_the_current_handler()
        => LiveDatabase.RunAsync(async database =>
        {
            using var first = database.CreateNode("previous", new FixedClock(Epoch));
            using var second = database.CreateNode("takeover", new FixedClock(Epoch.AddSeconds(3)));
            var ticker = await AddTimeAsync(first, Epoch.AddSeconds(1));
            var snapshot = Assert.Single(await Store(first).GetEarliestTimeTickers());
            var original = Assert.Single(await CollectAsync(Store(first).QueueTimeTickers([snapshot])));
            var acquired = Assert.Single(await CollectAsync(Store(second).QueueTimedOutTimeTickers()));
            Assert.NotEqual(Guid.Empty, acquired.ClaimToken);
            Assert.NotEqual(original.ClaimToken, acquired.ClaimToken);

            var stale = ClaimContext(original);
            Assert.False(await ClaimStore(first).ReleaseTimeTickerAsync(stale));
            Assert.False(await ClaimStore(first).TryRenewTimeTickerAsync(stale));
            Assert.Equal(0, await Store(first).UpdateTimeTicker(
                stale.SetProperty(x => x.Status, TickerStatus.Done)));
            await AssertTimeClaimAsync(database, ticker.Id, "takeover", TickerStatus.InProgress, Epoch.AddSeconds(3));

            await ExecuteAsync(first, ClaimContext(original));
            await Task.WhenAll(ExecuteAsync(second, ClaimContext(acquired)), ExecuteAsync(second, ClaimContext(acquired)));
            Assert.False(first.Services.GetRequiredService<ExecutionProbe>().Attempts.ContainsKey(ticker.Id));
            Assert.Equal(1, second.Services.GetRequiredService<ExecutionProbe>().Attempts[ticker.Id]);
            Assert.Equal(TickerStatus.Failed, (await ReadJobAsync(database, "TimeTickers", ticker.Id)).Status);
        });

    [Fact]
    public Task Same_node_restart_invalidates_prior_claim_and_allows_current_release_and_renewal()
        => LiveDatabase.RunAsync(async database =>
        {
            const string name = "same-node";
            using var first = database.CreateNode(name, new FixedClock(Epoch));
            using var restarted = database.CreateNode(name, new FixedClock(Epoch));
            var ticker = await AddTimeAsync(first, Epoch.AddSeconds(1));
            var snapshot = Assert.Single(await Store(first).GetEarliestTimeTickers());
            var old = Assert.Single(await CollectAsync(Store(first).QueueTimeTickers([snapshot])));
            await Store(restarted).ReleaseDeadNodeTimeTickerResources(name);

            var currentSnapshot = Assert.Single(await Store(restarted).GetEarliestTimeTickers());
            var current = Assert.Single(await CollectAsync(Store(restarted).QueueTimeTickers([currentSnapshot])));
            Assert.NotEqual(old.ClaimToken, current.ClaimToken);
            Assert.False(await ClaimStore(first).ReleaseTimeTickerAsync(ClaimContext(old)));
            await AssertTimeClaimAsync(database, ticker.Id, name, TickerStatus.Queued, Epoch);
            Assert.True(await ClaimStore(restarted).ReleaseTimeTickerAsync(ClaimContext(current)));

            var immediate = Assert.Single(await Store(restarted).AcquireImmediateTimeTickersAsync([ticker.Id]));
            var currentContext = ClaimContext(immediate);
            Assert.True(await ClaimStore(restarted).TryStartTimeTickerAsync(currentContext));
            Assert.False(await ClaimStore(first).TryRenewTimeTickerAsync(ClaimContext(old)));
            Assert.Equal(0, await Store(first).UpdateTimeTicker(
                ClaimContext(old).SetProperty(x => x.Status, TickerStatus.Done)));
            Assert.True(await ClaimStore(restarted).TryRenewTimeTickerAsync(currentContext));
            Assert.Equal(1, await Store(restarted).UpdateTimeTicker(
                currentContext.SetProperty(x => x.Status, TickerStatus.Done)));
            await AssertTimeClaimAsync(database, ticker.Id, name, TickerStatus.Done, Epoch);
        });

    [Fact]
    public Task Native_handler_executes_and_skips_chain_descendants_under_the_root_claim()
        => LiveDatabase.RunAsync(async database =>
        {
            using var node = database.CreateNode("chain", new FixedClock(Epoch));
            var grandchild = ChainJob(RunCondition.OnFailure);
            var child = ChainJob(RunCondition.OnFailure);
            child.Children.Add(grandchild);
            var skipped = ChainJob(RunCondition.OnSuccess);
            var root = ChainJob(RunCondition.OnAnyCompletedStatus);
            root.ExecutionTime = Epoch.AddSeconds(1);
            root.Children = [child, skipped];
            var manager = node.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
            RequireSuccess(await manager.AddAsync(root));
            var claim = Assert.Single(await Store(node).AcquireImmediateTimeTickersAsync([root.Id]));
            await ExecuteAsync(node, ClaimContext(claim));

            var attempts = node.Services.GetRequiredService<ExecutionProbe>().Attempts;
            Assert.Equal(1, attempts[root.Id]);
            Assert.Equal(1, attempts[child.Id]);
            Assert.Equal(1, attempts[grandchild.Id]);
            Assert.False(attempts.ContainsKey(skipped.Id));
            Assert.Equal(TickerStatus.Failed, (await ReadJobAsync(database, "TimeTickers", root.Id)).Status);
            Assert.Equal(TickerStatus.Failed, (await ReadJobAsync(database, "TimeTickers", child.Id)).Status);
            Assert.Equal(TickerStatus.Failed, (await ReadJobAsync(database, "TimeTickers", grandchild.Id)).Status);
            Assert.Equal(TickerStatus.Skipped, (await ReadJobAsync(database, "TimeTickers", skipped.Id)).Status);
        });

    [Fact]
    public Task Startup_seeder_dispatch_is_not_reclaimed_by_startup_recovery()
        => LiveDatabase.RunAsync(async database =>
        {
            var id = Guid.NewGuid();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            ExecutionProbe probe = null!;
            using var node = database.CreateNode("seeded-node", runScheduler: true, configure: options =>
                options.UseTickerSeeder(async (ITimeTickerManager<TimeTickerEntity> manager) =>
                {
                    RequireSuccess(await manager.AddAsync(new TimeTickerEntity
                    {
                        Id = id,
                        Function = StartupFunction,
                        ExecutionTime = DateTime.UtcNow
                    }, deadline.Token));
                    await probe.StartupEntered.Task.WaitAsync(deadline.Token);
                }));
            probe = node.Services.GetRequiredService<ExecutionProbe>();
            try
            {
                RegisterJob();
                node.UseTickerQ();
                var running = await ReadJobAsync(database, "TimeTickers", id);
                Assert.Equal(TickerStatus.InProgress, running.Status);
                Assert.Equal("seeded-node", running.Owner);
                await node.StartAsync(deadline.Token);
                probe.StartupContinue.TrySetResult();
                while ((await ReadJobAsync(database, "TimeTickers", id)).Status != TickerStatus.Done)
                    await Task.Delay(100, deadline.Token);
                Assert.Equal(1, probe.Attempts[id]);
            }
            finally
            {
                probe.StartupContinue.TrySetResult();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await node.StopAsync(stop.Token);
            }
        });

    [Fact]
    public Task Manager_can_reschedule_queued_job_and_invalidates_the_old_schedule()
        => LiveDatabase.RunAsync(async database =>
        {
            using var node = database.CreateNode("reschedule", new FixedClock(Epoch));
            var ticker = await AddTimeAsync(node, Epoch.AddSeconds(1));
            var snapshot = Assert.Single(await Store(node).GetEarliestTimeTickers());
            var old = Assert.Single(await CollectAsync(Store(node).QueueTimeTickers([snapshot])));
            var editable = await Store(node).GetTimeTickerById(ticker.Id);
            editable.ExecutionTime = Epoch.AddMinutes(1);
            var manager = node.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
            RequireSuccess(await manager.UpdateAsync(editable));

            Assert.False(await ClaimStore(node).ReleaseTimeTickerAsync(ClaimContext(old)));
            await ExecuteAsync(node, ClaimContext(old));
            Assert.Empty(node.Services.GetRequiredService<ExecutionProbe>().Attempts);
            var refreshed = Assert.Single(await Store(node).GetEarliestTimeTickers());
            Assert.Equal(Epoch.AddMinutes(1).Ticks, refreshed.ExecutionTime?.Ticks);
            Assert.NotEqual(old.ClaimToken, refreshed.ClaimToken);

            var newClaim = Assert.Single(await CollectAsync(Store(node).QueueTimeTickers([refreshed])));
            var staleEdit = await Store(node).GetTimeTickerById(ticker.Id);
            Assert.True(await ClaimStore(node).TryStartTimeTickerAsync(ClaimContext(newClaim)));
            staleEdit.ExecutionTime = Epoch.AddMinutes(2);
            var staleResult = await manager.UpdateAsync(staleEdit);
            Assert.False(staleResult.IsSucceeded);
            Assert.IsType<DbUpdateConcurrencyException>(staleResult.Exception);
            await AssertTimeClaimAsync(database, ticker.Id, "reschedule", TickerStatus.InProgress, Epoch);
            await AssertClaimTokenAsync(database, ticker.Id, newClaim.ClaimToken);
        });

    [Fact]
    public Task Restarted_chain_rejects_previous_generation_child_mutations()
        => LiveDatabase.RunAsync(async database =>
        {
            const string name = "chain-restart";
            using var oldNode = database.CreateNode(name, new FixedClock(Epoch));
            using var newNode = database.CreateNode(name, new FixedClock(Epoch));
            var child = ChainJob(RunCondition.OnAnyCompletedStatus);
            var root = ChainJob(RunCondition.OnAnyCompletedStatus);
            root.ExecutionTime = Epoch.AddSeconds(1);
            root.Children.Add(child);
            RequireSuccess(await oldNode.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>().AddAsync(root));
            var oldRoot = Assert.Single(await Store(oldNode).AcquireImmediateTimeTickersAsync([root.Id]));
            Assert.True(await ClaimStore(oldNode).TryStartTimeTickerAsync(ClaimContext(oldRoot)));
            var oldChild = ChildClaimContext(oldRoot);
            Assert.True(await ClaimStore(oldNode).TryStartTimeTickerAsync(oldChild));
            await Store(newNode).ReleaseDeadNodeTimeTickerResources(name);
            var currentRoot = Assert.Single(await Store(newNode).AcquireImmediateTimeTickersAsync([root.Id]));
            Assert.True(await ClaimStore(newNode).TryStartTimeTickerAsync(ClaimContext(currentRoot)));
            var currentChild = ChildClaimContext(currentRoot);
            Assert.True(await ClaimStore(newNode).TryStartTimeTickerAsync(currentChild));

            Assert.False(await ClaimStore(oldNode).TryRenewTimeTickerAsync(oldChild));
            Assert.Equal(0, await Store(oldNode).UpdateTimeTicker(oldChild.SetProperty(x => x.Status, TickerStatus.Done)));
            Assert.Equal(TickerStatus.InProgress, (await ReadJobAsync(database, "TimeTickers", child.Id)).Status);
            Assert.Equal(1, await Store(newNode).UpdateTimeTicker(currentChild.SetProperty(x => x.Status, TickerStatus.Done)));
            Assert.Equal(TickerStatus.Done, (await ReadJobAsync(database, "TimeTickers", child.Id)).Status);
        });

    private const string StartupFunction = "DamengStartupAcceptance";

    private static async Task HoldStartupJobAsync(CancellationToken token, IServiceProvider services, TickerFunctionContext context)
    {
        var probe = services.GetRequiredService<ExecutionProbe>();
        probe.Attempts.AddOrUpdate(context.Id, 1, (_, count) => count + 1);
        probe.StartupEntered.TrySetResult();
        await probe.StartupContinue.Task.WaitAsync(token);
    }

    private static InternalFunctionContext ChildClaimContext(TimeTickerEntity root)
    {
        var child = ClaimContext(Assert.Single(root.Children));
        child.TimeTickerClaimToken = root.ClaimToken;
        child.TimeTickerClaimRootId = root.Id;
        return child;
    }

    private static async Task AssertClaimTokenAsync(LiveDatabase database, Guid id, Guid expected)
    {
        var token = await database.QueryAsync("""
            SELECT "ClaimToken" FROM "TimeTickers" WHERE "Id" = :id
            """, async reader =>
        {
            Assert.True(await reader.ReadAsync());
            return Guid.Parse(reader.GetString(0));
        }, id);
        Assert.Equal(expected, token);
    }

    private static TimeTickerEntity ChainJob(RunCondition condition)
        => new() { Function = Function, Request = RequestBytes, RunCondition = condition };

    private static ITimeTickerClaimStore ClaimStore(IHost node) => Assert.IsAssignableFrom<ITimeTickerClaimStore>(Store(node));

    private static InternalFunctionContext ClaimContext(TimeTickerEntity claim)
        => new()
        {
            TickerId = claim.Id,
            Type = TickerType.TimeTicker,
            ParentId = claim.ParentId,
            FunctionName = Function,
            CachedDelegate = FailJobAsync,
            TimeTickerClaimToken = claim.ClaimToken,
            ExecutionTime = claim.ExecutionTime ?? Epoch,
            RunCondition = claim.RunCondition ?? RunCondition.OnAnyCompletedStatus,
            TimeTickerChildren = claim.Children.Select(ClaimContext).ToList()
        };

    private static Task ExecuteAsync(IHost node, InternalFunctionContext claim)
        => node.Services.GetRequiredService<ITickerExecutionTaskHandler>().ExecuteTaskAsync(claim, isDue: false);
}
#endif
