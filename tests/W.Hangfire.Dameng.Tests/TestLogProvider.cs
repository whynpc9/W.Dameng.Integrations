using Hangfire.Logging;
using W.Dm;

namespace W.Hangfire.Dameng.Tests;

internal sealed class TestLogProvider : ILogProvider
{
    private static readonly object Sync = new();
    public ILog GetLogger(string name) => new Logger(name);

    private sealed class Logger(string name) : ILog
    {
        public bool Log(LogLevel level, Func<string>? message, Exception? exception = null)
        {
            if (level < LogLevel.Debug) return false;
            if (exception != null)
            {
                var cause = exception.GetBaseException();
                var error = cause is DmException dm ? $" server={dm.Number}, driver={dm.DriverErrorCode}" : "";
                var line = $"{name}: {level}: {cause.GetType().Name}{error}";
                if (Environment.GetEnvironmentVariable("WHF_TEST_LOG") is { } path)
                {
                    lock (Sync) File.AppendAllText(path, line + Environment.NewLine);
                }
                else Console.WriteLine(line);
            }
            return true;
        }
    }
}
