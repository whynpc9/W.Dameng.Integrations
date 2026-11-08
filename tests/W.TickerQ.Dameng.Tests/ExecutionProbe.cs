using System.Collections.Concurrent;

namespace W.TickerQ.Dameng.Tests;

internal sealed class ExecutionProbe
{
    internal TaskCompletionSource StartupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StartupContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ConcurrentDictionary<Guid, int> Attempts { get; } = new();
}
