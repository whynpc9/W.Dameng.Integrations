using Microsoft.EntityFrameworkCore;

namespace W.Dameng.NamingConventions.Tests;

internal sealed class IdentifierContext(DbContextOptions options, string schema) : DbContext(options)
{
    internal const string TableName = "identifier_limits";
    internal static readonly string ColumnOne = "Value" + new string('a', 123) + "1";
    internal static readonly string ColumnTwo = "Value" + new string('a', 123) + "2";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(schema);
        model.Entity<Row>(entity =>
        {
            entity.ToTable(TableName);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property<int>(ColumnOne);
            entity.Property<int>(ColumnTwo);
            entity.HasIndex(ColumnOne);
            entity.HasIndex(ColumnTwo);
        });
    }

    internal sealed class Row
    {
        public int Id { get; set; }
    }
}
