using W.Hangfire.Dameng;

namespace PackageValidation;

public static class HangfireConsumer
{
    public static void Configure(global::Hangfire.IGlobalConfiguration configuration, string connection)
        => configuration.UseDamengStorage(connection);
}
