using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace W.TickerQ.Dameng.Tests;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DamengTickerQDbContext>
{
    public DamengTickerQDbContext CreateDbContext(string[] args)
    {
        // The design-time factory is only for scaffolding and script generation.
        var options = new DbContextOptionsBuilder<DamengTickerQDbContext>();
        options.UseDamengTickerQ("Server=localhost;User=TICKERQ_DESIGN", typeof(DesignTimeFactory).Assembly.GetName().Name);
        return new DamengTickerQDbContext(options.Options);
    }
}
