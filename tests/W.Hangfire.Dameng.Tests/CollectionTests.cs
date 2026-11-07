using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Xunit;
using static W.Hangfire.Dameng.Tests.TestDatabase;

namespace W.Hangfire.Dameng.Tests;

public sealed partial class StorageTests
{
    [Fact]
    public async Task CollectionsParametersAndLargeUnicodePayloadRoundTrip()
    {
        var storage = Open();
        var key = Queue();
        var value = string.Concat(Enumerable.Repeat("中文🚀é", 8000));
        var id = new BackgroundJobClient(storage).Create(Job.FromExpression(() => Work.Record(key, value, 0)),
            new EnqueuedState(key));
        using var connection = (JobStorageConnection)storage.GetConnection();
        using (var tx = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            tx.SetJobParameter(id, "MiXeD", value);
            tx.SetJobParameter(id, "empty", "");
            tx.SetJobParameter(id, "null", null);
            tx.AddToSet(key, "a", 7);
            tx.AddToSet(key, "A", 1);
            tx.AddToSet(key, "a", 2);
            tx.AddToSet(key, "", 4);
            tx.AddRangeToSet(key + ":batch", new List<string> { "first", "second" });
            tx.SetRangeInHash(key, new Dictionary<string, string> { ["k"] = value, ["K"] = "" });
            tx.InsertToList(key, "third");
            tx.InsertToList(key, "second");
            tx.InsertToList(key, "first");
            tx.TrimList(key, 1, 2);
            tx.ExpireHash(key, TimeSpan.FromMinutes(2));
            tx.IncrementCounter(key);
            tx.Commit();
        }

        using var read = (JobStorageConnection)Open().GetConnection();
        Assert.Equal(value, read.GetJobData(id)!.Job.Args[1]);
        Assert.True(Number("SELECT LENGTH(PAYLOAD) FROM WHF_JOB WHERE ID = :p0", Id(id)) > 30000);
        Assert.Equal(value, read.GetJobParameter(id, "MiXeD"));
        Assert.Equal("", read.GetJobParameter(id, "empty"));
        Assert.Null(read.GetJobParameter(id, "null"));
        Assert.Null(read.GetJobParameter(id, "mixed"));
        Assert.Equal(new[] { "A", "a", "" }, read.GetFirstByLowestScoreFromSet(key, 0, 10, 10));
        Assert.Equal(new[] { "a", "" }, read.GetRangeFromSet(key, 1, 2));
        Assert.True(read.GetSetContains(key, ""));
        Assert.Equal(2, read.GetSetCount([key], 2));
        Assert.Equal(3, read.GetSetCount([key, key], 10));
        Assert.Equal(new[] { "first", "second" }, read.GetFirstByLowestScoreFromSet(key + ":batch", 0, 0, 2));
        Assert.Equal(new[] { "second", "third" }, read.GetAllItemsFromList(key));
        Assert.Equal(new[] { "third" }, read.GetRangeFromList(key, 1, 1));
        Assert.Equal(value, read.GetValueFromHash(key, "k"));
        Assert.Equal("", read.GetValueFromHash(key, "K"));
        Assert.InRange(read.GetHashTtl(key), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var writer = storage.GetConnection();
            using var tx = writer.CreateWriteTransaction();
            tx.IncrementCounter(key);
            tx.Commit();
        })));
        Assert.Equal(9, read.GetCounter(key));
    }
}
