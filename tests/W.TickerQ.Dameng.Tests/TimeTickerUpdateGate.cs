using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace W.TickerQ.Dameng.Tests;

// Installed only on the second fallback node. Hold the real command before execution.
internal sealed class TimeTickerUpdateGate : DbCommandInterceptor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Entered => _entered.Task;

    internal void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.TrimStart().StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("\"TimeTickers\"", StringComparison.Ordinal))
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }

        return result;
    }
}
