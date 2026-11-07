namespace W.Hangfire.Dameng;

public sealed class DamengStorageOptions
{
    public TimeSpan QueuePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan InvisibilityTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan ExpirationCheckInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SchemaLockTimeout { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        ValidateInterval(QueuePollInterval, nameof(QueuePollInterval));
        ValidateInterval(InvisibilityTimeout, nameof(InvisibilityTimeout));
        ValidateInterval(LeaseRenewalInterval, nameof(LeaseRenewalInterval));
        ValidateInterval(ExpirationCheckInterval, nameof(ExpirationCheckInterval));
        ValidateInterval(SchemaLockTimeout, nameof(SchemaLockTimeout));
        if (LeaseRenewalInterval >= InvisibilityTimeout / 2)
            throw new ArgumentOutOfRangeException(nameof(LeaseRenewalInterval),
                "Lease renewal must run more than twice per invisibility interval.");
    }

    private static void ValidateInterval(TimeSpan interval, string name)
    {
        if (interval < TimeSpan.FromMilliseconds(1) || interval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(name);
    }
}
