using W.Dm;
using W.Hangfire.Dameng.Tests;

try
{
    var storage = TestDatabase.Open();
    using var connection = storage.GetConnection();
    switch (args)
    {
        case ["hold-lock", var resource]:
            using (connection.AcquireDistributedLock(resource, TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine("READY");
                Thread.Sleep(Timeout.Infinite);
            }
            break;
        case ["hold-job", var queue]:
            using (var fetched = connection.FetchNextJob([queue], new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
            {
                Console.WriteLine("READY " + fetched.JobId);
                Thread.Sleep(Timeout.Infinite);
            }
            break;
        default:
            return 2;
    }

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(ex is DmException dm ? $"ERROR {dm.Number}" : $"ERROR {ex.GetType().Name}");
    return 1;
}
