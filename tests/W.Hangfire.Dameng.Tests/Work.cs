using Hangfire;

namespace W.Hangfire.Dameng.Tests;

public static class Work
{
    [AutomaticRetry(Attempts = 0, LogEvents = false)]
    public static void Record(string run, string value, int milliseconds = 0)
    {
        Thread.Sleep(milliseconds);
        TestDatabase.Execute("INSERT INTO WHF_TEST_EXECUTION (RUN_ID, RESULT_VALUE) VALUES (:p0, :p1)", run, value);
    }

    [AutomaticRetry(Attempts = 0, LogEvents = false)]
    public static void Fail() => throw new InvalidOperationException("Hangfire live test failure.");
}
