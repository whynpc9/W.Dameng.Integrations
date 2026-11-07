using System.Collections.Concurrent;

namespace W.TickerQ.Dameng.Tests;

internal sealed class ExecutionProbe
{
    internal ConcurrentDictionary<Guid, int> Attempts { get; } = new();
}
