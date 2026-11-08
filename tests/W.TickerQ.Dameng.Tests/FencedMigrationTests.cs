#if TICKERQ_F14_SEED || TICKERQ_F14_MIGRATION
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Models;
using Xunit;

namespace W.TickerQ.Dameng.Tests;

public sealed partial class OperationalStoreTests
{
    private static readonly Guid LegacyTime = Guid.Parse("9773cb70-b6c5-4ae0-8906-9607b71ec78d");
    private static readonly Guid LegacyCron = Guid.Parse("b76a6425-b6c5-4ae0-8906-9607b71ec78d");

#if TICKERQ_F14_SEED
    [Fact]
    public Task Seed_published_legacy_data()
        => LiveDatabase.RunAsync(async database =>
        {
            using var node = database.CreateNode("legacy", new FixedClock(Epoch));
            await AddTimeAsync(node, Epoch.AddMinutes(1), [7, 13], id: LegacyTime);
            var cron = await AddCronAsync(node, [3, 17], id: LegacyCron);
            var snapshot = Assert.Single(await Store(node).GetEarliestTimeTickers());
            Assert.Single(await CollectAsync(Store(node).QueueTimeTickers([snapshot])));
            var occurrence = Assert.Single(await QueueCronAsync(node, cron, Epoch.AddMinutes(1)));
            await Store(node).UpdateCronTickerOccurrence(new InternalFunctionContext { TickerId = occurrence.Id }
                .SetProperty(x => x.Status, TickerStatus.Failed)
                .SetProperty(x => x.ExceptionDetails, LongException));
            await AssertLegacyRowsAsync(database);
        });
#endif

#if TICKERQ_F14_MIGRATION
    [Fact]
    public Task Migration_preserves_published_legacy_data()
        => LiveDatabase.RunAsync(async database =>
        {
            await AssertLegacyRowsAsync(database);
            using var node = database.CreateNode("legacy", new FixedClock(Epoch.AddMinutes(2)));
            var original = await Store(node).GetTimeTickerById(LegacyTime);
            Assert.Equal(Guid.Empty, original.ClaimToken);
            Assert.Equal(TickerStatus.Queued, original.Status);
            await Store(node).ReleaseDeadNodeTimeTickerResources("legacy");
            var claimed = Assert.Single(await CollectAsync(Store(node).QueueTimedOutTimeTickers()));
            Assert.Equal(LegacyTime, claimed.Id);
            Assert.NotEqual(Guid.Empty, claimed.ClaimToken);
            await AssertRawRequestAndIntervalsAsync(database, "TimeTickers", LegacyTime, [7, 13]);
        }, reset: false);
#endif

    private static async Task AssertLegacyRowsAsync(LiveDatabase database)
    {
        Assert.Equal(1, await database.CountAsync("SELECT COUNT(*) FROM \"TimeTickers\""));
        Assert.Equal(1, await database.CountAsync("SELECT COUNT(*) FROM \"CronTickers\""));
        Assert.Equal(1, await database.CountAsync("SELECT COUNT(*) FROM \"CronTickerOccurrences\""));
        await AssertRawRequestAndIntervalsAsync(database, "TimeTickers", LegacyTime, [7, 13]);
        await AssertRawRequestAndIntervalsAsync(database, "CronTickers", LegacyCron, [3, 17]);
        await AssertTimeClaimAsync(database, LegacyTime, "legacy", TickerStatus.Queued, Epoch);
        using var node = database.CreateNode("migration-read");
        var occurrences = await Store(node).GetAllCronTickerOccurrences(x => x.CronTickerId == LegacyCron);
        var occurrence = Assert.Single(occurrences);
        var persisted = await ReadJobAsync(database, "CronTickerOccurrences", occurrence.Id);
        Assert.Equal(TickerStatus.Failed, persisted.Status);
        Assert.True(persisted.Exception == LongException, "Legacy occurrence exception changed.");
        Assert.Equal(Epoch.AddMinutes(1).Ticks, occurrence.ExecutionTime.Ticks);
    }
}
#endif
