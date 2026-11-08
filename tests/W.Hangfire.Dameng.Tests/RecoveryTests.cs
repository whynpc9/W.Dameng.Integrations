using System.Diagnostics;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using W.Dm;
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
    public async Task DistributedLockIsReentrantTimesOutAndReleasesAfterProcessDeath()
    {
        var storage = Open();
        var resource = Queue();
        using var first = storage.GetConnection();
        using var second = Open().GetConnection();
        using (first.AcquireDistributedLock(resource, TimeSpan.FromSeconds(2)))
        {
            using (first.AcquireDistributedLock(resource, TimeSpan.Zero))
                Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock(resource, TimeSpan.FromMilliseconds(200)));
            Assert.Throws<DistributedLockTimeoutException>(() => second.AcquireDistributedLock(resource, TimeSpan.Zero));
        }

        using (second.AcquireDistributedLock(resource, TimeSpan.FromSeconds(2))) { }
        using var child = Spawn("hold-lock", resource);
        try
        {
            Assert.Equal("READY", await Ready(child));
            Assert.Throws<DistributedLockTimeoutException>(() => first.AcquireDistributedLock(resource, TimeSpan.FromMilliseconds(200)));
            child.Kill();
            await child.WaitForExitAsync();
            using (first.AcquireDistributedLock(resource, TimeSpan.FromSeconds(10))) { }
        }
        finally { Stop(child); }
    }

    [Fact]
    public async Task LockReacquisitionWaitsForTheDisconnectedOwnersInFlightWrite()
    {
        var storage = Open();
        var resource = Queue();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));
        using (var seed = storage.GetConnection())
        using (seed.AcquireDistributedLock(resource, TimeSpan.FromSeconds(10))) { }

        // Emulate the two physical sessions of an owner after its final lock check.
        using var lockSession = OpenVerified();
        using var lockTransaction = lockSession.BeginTransaction(IsolationLevel.ReadCommitted, false);
        using (var command = lockSession.CreateCommand("SELECT K FROM WHF_LOCK WHERE K = :p0 FOR UPDATE"))
        {
            command.Transaction = lockTransaction;
            command.Parameters.Add(new DmParameter("p0", key));
            Assert.Equal(key, command.ExecuteScalar());
        }

        using var writer = OpenVerified();
        using var writeTransaction = writer.BeginTransaction(IsolationLevel.ReadCommitted, false);
        using (var command = writer.CreateCommand("SELECT K FROM WHF_LOCK WHERE K = 'WRITER' FOR UPDATE"))
        {
            command.Transaction = writeTransaction;
            Assert.Equal("WRITER", command.ExecuteScalar());
        }

        lockSession.Close();
        using var contender = storage.GetConnection();
        await Task.Run(() => Assert.Throws<DistributedLockTimeoutException>(() =>
            contender.AcquireDistributedLock(resource, TimeSpan.FromSeconds(2))));
        writeTransaction.Commit();
        using (contender.AcquireDistributedLock(resource, TimeSpan.FromSeconds(10))) { }
    }

    [Fact]
    public async Task WorkerRecoversAnUnacknowledgedJobAfterProcessDeath()
    {
        EnsureExecutionTable();
        var storage = Open();
        var queue = Queue();
        var id = new BackgroundJobClient(storage).Create(Job.FromExpression(() => Work.Record(queue, "recovered", 0)), new EnqueuedState(queue));
        using var child = Spawn("hold-job", queue);
        try
        {
            Assert.Equal("READY " + id, await Ready(child));
            var monitoring = storage.GetMonitoringApi();
            Assert.Equal(0, monitoring.EnqueuedCount(queue));
            Assert.Equal(1, monitoring.FetchedCount(queue));
            Assert.Equal(id, Assert.Single(monitoring.FetchedJobs(queue, 0, 1)).Key);
            Assert.Equal(1, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0 AND LEASE_TOKEN IS NOT NULL", Id(id)));

            child.Kill();
            await child.WaitForExitAsync();
            using var restarted = StartServer(OpenWith(new DamengStorageOptions()), queue, 1, "recovered", lightweight: true);
            await Until(() => State(id) == SucceededState.StateName);
            Assert.Equal(1, Number("SELECT COUNT(*) FROM WHF_TEST_EXECUTION WHERE RUN_ID = :p0 AND RESULT_VALUE = 'recovered'", queue));
            Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(id)));
        }
        finally { Stop(child); }
    }

    [Fact]
    public async Task StaleLeaseCannotAcknowledgeRequeueOrCommitStateOfNewOwner()
    {
        var storage = Open();
        var queue = Queue();
        var id = new BackgroundJobClient(storage).Create(Job.FromExpression(() => Work.Record(queue, "stale", 0)), new EnqueuedState(queue));
        using var first = storage.GetConnection();
        using var second = OpenWith(new DamengStorageOptions()).GetConnection();
        using var firstFetchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        using var stale = first.FetchNextJob([queue], firstFetchTimeout.Token);
        stale.Dispose(); // A lost worker stops renewing without acknowledging.
        await Until(() => Number("""
            SELECT COUNT(*) FROM WHF_QUEUE
            WHERE JOB_ID = :p0 AND LEASE_UNTIL <= SYS_EXTRACT_UTC(CURRENT_TIMESTAMP())
            """, Id(id)) == 1);
        stale.RemoveFromQueue();
        stale.Requeue();
        Assert.Equal(1, Number("""
            SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0 AND LEASE_TOKEN IS NOT NULL
            """, Id(id)));

        using var nextFetchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        using var fresh = second.FetchNextJob([queue], nextFetchTimeout.Token);
        Assert.Equal(id, fresh.JobId);

        stale.RemoveFromQueue();
        stale.Requeue();
        Assert.Equal(1, storage.GetMonitoringApi().FetchedCount(queue));
        using var oldTransaction = (JobStorageTransaction)first.CreateWriteTransaction();
        oldTransaction.SetJobState(id, new DeletedState());
        oldTransaction.RemoveFromQueue(stale);
        Assert.Throws<InvalidOperationException>(oldTransaction.Commit);
        Assert.Equal("Enqueued", State(id));
        Assert.Equal(1, Number("SELECT COUNT(*) FROM WHF_STATE WHERE JOB_ID = :p0", Id(id)));

        using var transaction = (JobStorageTransaction)second.CreateWriteTransaction();
        transaction.SetJobState(id, new DeletedState());
        transaction.RemoveFromQueue(fresh);
        transaction.Commit();
        Assert.Equal("Deleted", State(id));
        Assert.Equal(0, Number("SELECT COUNT(*) FROM WHF_QUEUE WHERE JOB_ID = :p0", Id(id)));
    }

    private static Process Spawn(string action, string argument)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(StorageTests).Assembly.Location);
        start.ArgumentList.Add(action);
        start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the crash probe.");
    }

    private static async Task<string?> Ready(Process child)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        return await child.StandardOutput.ReadLineAsync(timeout.Token);
    }

    private static void Stop(Process child)
    {
        if (!child.HasExited) child.Kill();
        child.WaitForExit();
    }
}
