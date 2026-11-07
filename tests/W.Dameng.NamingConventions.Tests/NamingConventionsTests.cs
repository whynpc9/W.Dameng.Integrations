using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using static W.Dameng.NamingConventions.Tests.LiveDatabase;

namespace W.Dameng.NamingConventions.Tests;

public sealed class NamingConventionsTests
{
    private const string History = "Naming-MigrationHistory";
    private const string MigrationId = "20261107135532_InitialNaming";
    private readonly LiveDatabase database = new();

    [Theory]
    [InlineData("snake", "invoice_lines", "item_label")]
    [InlineData("lower", "invoicelines", "itemlabel")]
    [InlineData("camel", "invoiceLines", "itemLabel")]
    [InlineData("upper", "INVOICELINES", "ITEMLABEL")]
    [InlineData("screaming", "INVOICE_LINES", "ITEM_LABEL")]
    public void Native_convention_creates_schema_qualified_objects_and_round_trips(
        string convention, string table, string column)
    {
        using var writer = database.Connect();
        using var reader = database.Connect();
        var options = new DbContextOptionsBuilder().UseDamengWithWDriver(writer);
        _ = convention switch
        {
            "snake" => options.UseSnakeCaseNamingConvention(),
            "lower" => options.UseLowerCaseNamingConvention(),
            "camel" => options.UseCamelCaseNamingConvention(),
            "upper" => options.UseUpperCaseNamingConvention(),
            "screaming" => options.UseUpperSnakeCaseNamingConvention(),
            _ => throw new ArgumentOutOfRangeException(nameof(convention))
        };
        using var context = new ConventionContext(options.Options, database.Schema);
        var entity = context.Model.FindEntityType(typeof(ConventionContext.Line))!;
        Assert.Equal(table, entity.GetTableName());
        Assert.Equal(database.Schema, entity.GetSchema());
        Assert.Equal(column, entity.FindProperty(nameof(ConventionContext.Line.ItemLabel))!
            .GetColumnName(StoreObjectIdentifier.Table(table, database.Schema)));

        DropTable(writer, table);
        try
        {
            context.GetService<IRelationalDatabaseCreator>().CreateTables();
            context.InvoiceLines.Add(new ConventionContext.Line { Id = 3, ItemLabel = "发票" });
            Assert.Equal(1, context.SaveChanges());
            Assert.Equal("发票", context.InvoiceLines.AsNoTracking().Single().ItemLabel);
            Assert.Contains(column, Columns(reader, table));
            Assert.Equal(database.Tablespace, Scalar(reader, $"SELECT TABLESPACE_NAME FROM USER_TABLES WHERE TABLE_NAME = {Literal(table)}"));
            Assert.Equal("发票", Scalar(reader, $"SELECT {Quote(column)} FROM {database.Table(table)}"));
        }
        finally
        {
            DropTable(writer, table);
        }
    }

    [Fact]
    public void Convention_uniquifies_truncated_columns_and_indexes()
    {
        using var writer = database.Connect();
        using var reader = database.Connect();
        var options = new DbContextOptionsBuilder().UseDamengWithWDriver(writer).UseSnakeCaseNamingConvention();
        using var context = new IdentifierContext(options.Options, database.Schema);
        var table = IdentifierContext.TableName;
        var model = context.GetService<IDesignTimeModel>().Model;
        Assert.Equal(128, model.GetMaxIdentifierLength());
        var entity = model.FindEntityType(typeof(IdentifierContext.Row))!;
        var store = StoreObjectIdentifier.Table(table, database.Schema);
        var columns = new[] { IdentifierContext.ColumnOne, IdentifierContext.ColumnTwo }
            .Select(name => entity.FindProperty(name)!.GetColumnName(store)!).ToArray();
        var indexes = entity.GetIndexes().Select(index => index.GetDatabaseName()!).ToArray();
        AssertTruncatedPair(columns);
        AssertTruncatedPair(indexes);

        DropTable(writer, table);
        try
        {
            context.GetService<IRelationalDatabaseCreator>().CreateTables();
            var row = context.Add(new IdentifierContext.Row { Id = 11 });
            row.Property(IdentifierContext.ColumnOne).CurrentValue = 7;
            row.Property(IdentifierContext.ColumnTwo).CurrentValue = 19;
            context.SaveChanges();

            var catalogColumns = Columns(reader, table);
            var catalogIndexes = database.Catalog(reader, "INDEX_NAME", "USER_INDEXES", $"TABLE_NAME = {Literal(table)}");
            Assert.All(columns, column => Assert.Contains(column, catalogColumns));
            Assert.All(indexes, index => Assert.Contains(index, catalogIndexes));
            Assert.Equal(7, Number(reader, $"SELECT {Quote(columns[0])} FROM {database.Table(table)}"));
            Assert.Equal(19, Number(reader, $"SELECT {Quote(columns[1])} FROM {database.Table(table)}"));
        }
        finally
        {
            DropTable(writer, table);
        }
    }

    [Fact]
    public void Expanded_Pascal_name_requires_explicit_short_mapping()
    {
        using var writer = database.Connect();
        using var reader = database.Connect();
        var options = new DbContextOptionsBuilder().UseDamengWithWDriver(writer).UseSnakeCaseNamingConvention();
        using var context = new ExpandedIdentifierContext(options.Options, database.Schema);
        var table = ExpandedIdentifierContext.TableName;
        var entity = context.Model.FindEntityType(typeof(ExpandedIdentifierContext.Row))!;
        var column = entity.FindProperty(ExpandedIdentifierContext.PropertyName)!
            .GetColumnName(StoreObjectIdentifier.Table(table, database.Schema))!;
        Assert.Equal(191, column.Length);
        Assert.StartsWith("ab_ab_ab_", column, StringComparison.Ordinal);

        DropTable(writer, table);
        try
        {
            var error = Assert.Throws<W.Dm.DmException>(() =>
                context.GetService<IRelationalDatabaseCreator>().CreateTables());
            Assert.Equal(-2051, error.Number);
            Assert.Empty(Columns(reader, table));

            using var mapped = new MappedIdentifierContext(
                new DbContextOptionsBuilder().UseDamengWithWDriver(reader).UseSnakeCaseNamingConvention().Options,
                database.Schema);
            mapped.GetService<IRelationalDatabaseCreator>().CreateTables();
            var row = mapped.Add(new ExpandedIdentifierContext.Row { Id = 1 });
            row.Property(ExpandedIdentifierContext.PropertyName).CurrentValue = 27;
            Assert.Equal(1, mapped.SaveChanges());

            using var verifier = database.Connect();
            Assert.Contains(MappedIdentifierContext.ColumnName, Columns(verifier, table));
            Assert.Equal(27, Number(verifier, $"SELECT {Quote(MappedIdentifierContext.ColumnName)} FROM {database.Table(table)}"));
        }
        finally
        {
            DropTable(reader, table);
        }
    }

    [Fact]
    public void Server_accepts_a_128_byte_quoted_identifier_and_rejects_129_bytes()
    {
        const string table = "identifier_byte_limit";
        var limit = "a" + new string('b', 127);
        using var writer = database.Connect();
        using var reader = database.Connect();
        DropTable(writer, table);
        try
        {
            Execute(writer, $"CREATE TABLE {database.Table(table)} ({Quote(limit)} INT)");
            Assert.Equal(new[] { limit }, Columns(reader, table));
            var tooLong = limit + "c";
            var error = Assert.Throws<W.Dm.DmException>(() =>
                Execute(writer, $"ALTER TABLE {database.Table(table)} ADD {Quote(tooLong)} INT"));
            Assert.Equal(-2051, error.Number);
            Assert.Equal(new[] { limit }, Columns(reader, table));
        }
        finally
        {
            DropTable(reader, table);
        }
    }

    [Fact]
    public async Task Migration_reruns_and_two_connections_can_reapply_after_rollback()
    {
        using var reader = database.Connect();
        ResetMigrationObjects(reader);
        try
        {
            using (var connection = database.Connect())
            using (var context = MigrationContext(connection))
            {
                Assert.False(context.Database.HasPendingModelChanges());
                await context.Database.MigrateAsync();
                WriteOrder(context);
            }

            using (var connection = database.Connect())
            using (var context = MigrationContext(connection))
            {
                context.Database.Migrate();
                Assert.Empty(context.Database.GetPendingMigrations());
                Assert.Equal(new[] { MigrationId }, context.Database.GetAppliedMigrations());
                Assert.Equal(1, context.OrderItems.AsNoTracking().Single().ItemCount);
                VerifyMigration(reader);
                context.GetService<IMigrator>().Migrate(Migration.InitialDatabase);
                Assert.Empty(context.Database.GetAppliedMigrations());
            }

            Assert.Empty(Columns(reader, "order_items"));
            Assert.Empty(Columns(reader, "purchase_orders"));
            Assert.Empty(Sequences(reader));

            using var firstConnection = database.Connect();
            using var secondConnection = database.Connect();
            using var first = MigrationContext(firstConnection);
            using var second = MigrationContext(secondConnection);
            await Task.WhenAll(first.Database.MigrateAsync(), second.Database.MigrateAsync());
            WriteOrder(first);
            VerifyMigration(reader);
        }
        finally
        {
            ResetMigrationObjects(reader);
        }
    }

    [Fact]
    public void Idempotent_script_with_current_schema_history_reapplies_without_losing_data()
    {
        using var writer = database.Connect();
        using var reader = database.Connect();
        using var context = MigrationContext(writer, qualifyHistory: false);
        var (history, blocks) = GenerateIdempotentBatches(context);
        Assert.StartsWith("CREATE TABLE IF NOT EXISTS ", history, StringComparison.Ordinal);

        ResetMigrationObjects(writer);
        try
        {
            ApplyScript();
            WriteOrder(context);
            ApplyScript();
            Assert.Equal(new[] { MigrationId }, context.Database.GetAppliedMigrations());
            VerifyMigration(reader);
        }
        finally
        {
            ResetMigrationObjects(reader);
        }

        void ApplyScript()
        {
            Execute(writer, history);
            foreach (var block in blocks)
                Execute(writer, block);
        }
    }

    [Fact]
    public void Idempotent_script_with_explicit_history_schema_has_an_unsplit_prologue()
    {
        using var writer = database.Connect();
        using var reader = database.Connect();
        using var context = MigrationContext(writer);
        var (history, _) = GenerateIdempotentBatches(context);
        Assert.Contains("END;CREATE TABLE IF NOT EXISTS ", history, StringComparison.Ordinal);

        ResetMigrationObjects(writer);
        try
        {
            var error = Assert.Throws<W.Dm.DmException>(() => Execute(writer, history));
            Assert.Equal(-2007, error.Number);
            Assert.Empty(Columns(reader, History));
            Assert.Empty(Sequences(reader));
            Assert.Empty(Columns(reader, "purchase_orders"));
        }
        finally
        {
            ResetMigrationObjects(reader);
        }
    }

    private static (string History, string[] Blocks) GenerateIdempotentBatches(NamingMigrationContext context)
    {
        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent
            | MigrationsSqlGenerationOptions.NoTransactions);
        var history = context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript().Trim();
        Assert.StartsWith(history, script, StringComparison.Ordinal);

        // This fixture emits history DDL followed by disql slash-delimited anonymous blocks.
        var blocks = System.Text.RegularExpressions.Regex.Split(script[history.Length..], @"(?m)^/\s*$")
            .Select(batch => batch.Trim()).Where(batch => batch.Length != 0).ToArray();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.StartsWith("BEGIN", block, StringComparison.Ordinal));
        return (history, blocks);
    }

    private void VerifyMigration(DbConnection reader)
    {
        Assert.Equal(new[] { "Order-Label", "order_id" }, Columns(reader, "purchase_orders"));
        Assert.Equal(new[] { "item_count", "item_id", "purchase_order_id" }, Columns(reader, "order_items"));
        var indexes = database.Catalog(reader, "INDEX_NAME", "USER_INDEXES", "TABLE_NAME = 'order_items'");
        Assert.Contains("ix_order_items_purchase_order_id", indexes);
        var constraints = database.Catalog(reader, "CONSTRAINT_NAME", "USER_CONSTRAINTS",
            "TABLE_NAME IN ('purchase_orders', 'order_items')");
        Assert.All(new[] { "pk_order_items", "pk_purchase_orders", "ak_purchase_orders_order_label",
            "fk_order_items_purchase_orders_purchase_order_id", "CK_PositiveCount" },
            name => Assert.Contains(name, constraints));
        Assert.Equal(new[] { "OrderNumberSequence" }, Sequences(reader));
        Assert.Equal(1000, Number(reader, $"SELECT {database.Table("OrderNumberSequence")}.NEXTVAL FROM DUAL"));
        Assert.Equal(new[] { "migration_id", "product_version" }, Columns(reader, History));
        Assert.Equal(MigrationId, Scalar(reader, $"SELECT \"migration_id\" FROM {database.Table(History)}"));
        Assert.Equal(1, Number(reader, $"SELECT COUNT(*) FROM {database.Table(History)}"));
        Assert.Equal("配件", Scalar(reader, $"SELECT \"Order-Label\" FROM {database.Table("purchase_orders")} WHERE \"order_id\" = 21"));
        Assert.Equal(1, Number(reader, $"SELECT \"item_count\" FROM {database.Table("order_items")} WHERE \"purchase_order_id\" = 21"));
    }

    private NamingMigrationContext MigrationContext(DbConnection connection, bool qualifyHistory = true)
        => new(new DbContextOptionsBuilder<NamingMigrationContext>()
            .UseDamengWithWDriver(connection, options => options.MigrationsHistoryTable(History, qualifyHistory ? database.Schema : null).CommandTimeout(30))
            .UseSnakeCaseNamingConvention().Options);

    private static void WriteOrder(NamingMigrationContext context)
    {
        context.PurchaseOrders.Add(new NamingMigrationContext.PurchaseOrder { OrderId = 21, OrderLabel = "配件" });
        context.OrderItems.Add(new NamingMigrationContext.OrderItem { ItemId = 34, PurchaseOrderId = 21 });
        Assert.Equal(2, context.SaveChanges());
    }

    private string[] Columns(DbConnection connection, string table)
        => database.Catalog(connection, "COLUMN_NAME", "USER_TAB_COLUMNS", $"TABLE_NAME = {Literal(table)}");

    private string[] Sequences(DbConnection connection)
        => database.Catalog(connection, "SEQUENCE_NAME", "USER_SEQUENCES", "SEQUENCE_NAME = 'OrderNumberSequence'");

    private void DropTable(DbConnection connection, string table)
        => Execute(connection, $"DROP TABLE IF EXISTS {database.Table(table)}");

    private void ResetMigrationObjects(DbConnection connection)
    {
        DropTable(connection, "order_items");
        DropTable(connection, "purchase_orders");
        DropTable(connection, History);
        Execute(connection, $"DROP SEQUENCE IF EXISTS {database.Table("OrderNumberSequence")}");
    }

    private static void AssertTruncatedPair(string[] names)
    {
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.InRange(name.Length, 127, 128));
        Assert.Equal(names[0][..125], names[1][..125]);
    }
}
