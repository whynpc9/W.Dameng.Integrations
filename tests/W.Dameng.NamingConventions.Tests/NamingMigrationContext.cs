using Microsoft.EntityFrameworkCore;

namespace W.Dameng.NamingConventions.Tests;

public sealed class NamingMigrationContext(DbContextOptions<NamingMigrationContext> options) : DbContext(options)
{
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasSequence<long>("OrderNumberSequence").StartsAt(1000);
        model.Entity<PurchaseOrder>(entity =>
        {
            entity.Property(order => order.OrderId).ValueGeneratedNever();
            entity.HasKey(order => order.OrderId);
            entity.Property(order => order.OrderLabel).HasColumnName("Order-Label").HasMaxLength(40);
            entity.HasAlternateKey(order => order.OrderLabel);
        });
        model.Entity<OrderItem>(entity =>
        {
            entity.Property(item => item.ItemId).ValueGeneratedNever();
            entity.HasKey(item => item.ItemId);
            entity.Property(item => item.ItemCount).HasDefaultValue(1);
            entity.HasOne<PurchaseOrder>().WithMany().HasForeignKey(item => item.PurchaseOrderId);
            entity.ToTable(table => table.HasCheckConstraint("CK_PositiveCount", "\"item_count\" > 0"));
        });
    }

    public sealed class PurchaseOrder
    {
        public int OrderId { get; set; }
        public string OrderLabel { get; set; } = null!;
    }

    public sealed class OrderItem
    {
        public int ItemId { get; set; }
        public int PurchaseOrderId { get; set; }
        public int ItemCount { get; set; }
    }
}
