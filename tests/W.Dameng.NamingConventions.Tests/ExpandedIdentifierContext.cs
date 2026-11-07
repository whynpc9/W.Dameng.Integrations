using Microsoft.EntityFrameworkCore;

namespace W.Dameng.NamingConventions.Tests;

internal class ExpandedIdentifierContext(DbContextOptions options, string schema) : DbContext(options)
{
    internal const string TableName = "expanded_identifier";
    internal static readonly string PropertyName = string.Concat(Enumerable.Repeat("Ab", 64));

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(schema);
        model.Entity<Row>(entity =>
        {
            entity.ToTable(TableName);
            entity.Property(row => row.Id).ValueGeneratedNever();
            var property = entity.Property<int>(PropertyName);
            if (ColumnOverride is { } name)
                property.HasColumnName(name);
        });
    }

    protected virtual string? ColumnOverride => null;

    internal sealed class Row
    {
        public int Id { get; set; }
    }
}


internal sealed class MappedIdentifierContext(DbContextOptions options, string schema) : ExpandedIdentifierContext(options, schema)
{
    internal const string ColumnName = "long_pascal_value";
    protected override string ColumnOverride => ColumnName;
}
