using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace W.FlexLabs.Upsert.Dameng;

/// <summary>Registers the FlexLabs upsert runner for UseDamengWithWDriver contexts.</summary>
public static class DamengUpsertExtensions
{
    public static DbContextOptionsBuilder UseDamengUpsert(this DbContextOptionsBuilder options)
        => options.ReplaceUpsertCommandRunner<DamengUpsertCommandRunner>();

    public static DbContextOptionsBuilder<TContext> UseDamengUpsert<TContext>(
        this DbContextOptionsBuilder<TContext> options) where TContext : DbContext
    {
        UseDamengUpsert((DbContextOptionsBuilder)options);
        return options;
    }
}
