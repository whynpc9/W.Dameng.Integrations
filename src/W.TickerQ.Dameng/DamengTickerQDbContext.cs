using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TickerQ.EntityFrameworkCore.Configurations;
using TickerQ.Utilities.Entities;

namespace W.TickerQ.Dameng;

/// <summary>
/// The native TickerQ tables, mapped into the current Dameng schema.
/// Applications own the EF migrations for this context.
/// </summary>
public sealed class DamengTickerQDbContext(DbContextOptions<DamengTickerQDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TimeTickerConfigurations<TimeTickerEntity>(schema: null));
        modelBuilder.ApplyConfiguration(new CronTickerConfigurations<CronTickerEntity>(schema: null));
        modelBuilder.ApplyConfiguration(new CronTickerOccurrenceConfigurations<CronTickerEntity>(schema: null));

        var time = modelBuilder.Entity<TimeTickerEntity>();
        time.Property(x => x.Request).HasColumnType("BLOB");
        time.Property(x => x.ExceptionMessage).HasColumnType("NCLOB");
        time.Property(x => x.SkippedReason).HasColumnType("NCLOB");
        time.Property(x => x.LockHolder).HasMaxLength(256);
        ConfigureRetryIntervals(time.Property(x => x.RetryIntervals));

        var cron = modelBuilder.Entity<CronTickerEntity>();
        cron.Property(x => x.Request).HasColumnType("BLOB");
        cron.Property(x => x.Function).HasMaxLength(256);
        cron.Property(x => x.Expression).HasMaxLength(512);
        ConfigureRetryIntervals(cron.Property(x => x.RetryIntervals));

        var occurrence = modelBuilder.Entity<CronTickerOccurrenceEntity<CronTickerEntity>>();
        occurrence.Property(x => x.ExceptionMessage).HasColumnType("NCLOB");
        occurrence.Property(x => x.SkippedReason).HasColumnType("NCLOB");
        occurrence.Property(x => x.LockHolder).HasMaxLength(256);

        // UpdatedAt is a compare-and-swap token; preserve every DateTime tick.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                property.SetPrecision(7);
        }
    }

    private static void ConfigureRetryIntervals(PropertyBuilder<int[]> property)
    {
        property.HasConversion(RetryIntervalMapping.Converter, RetryIntervalMapping.Comparer)
            .HasColumnType("CLOB");
    }
}
