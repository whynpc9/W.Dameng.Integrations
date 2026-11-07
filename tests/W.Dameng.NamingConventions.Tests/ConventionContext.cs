using Microsoft.EntityFrameworkCore;

namespace W.Dameng.NamingConventions.Tests;

internal sealed class ConventionContext(DbContextOptions options, string schema) : DbContext(options)
{
    public DbSet<Line> InvoiceLines => Set<Line>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(schema);
        model.Entity<Line>().Property(line => line.Id).ValueGeneratedNever();
    }

    internal sealed class Line
    {
        public int Id { get; set; }
        public string ItemLabel { get; set; } = null!;
    }
}
