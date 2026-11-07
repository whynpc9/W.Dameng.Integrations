using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace W.Dameng.NamingConventions.Tests.Migrations
{
    /// <inheritdoc />
    public partial class InitialNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "OrderNumberSequence",
                startValue: 1000L);

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    order_id = table.Column<int>(type: "INT", nullable: false),
                    OrderLabel = table.Column<string>(name: "Order-Label", type: "NVARCHAR2(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_orders", x => x.order_id);
                    table.UniqueConstraint("ak_purchase_orders_order_label", x => x.OrderLabel);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    item_id = table.Column<int>(type: "INT", nullable: false),
                    purchase_order_id = table.Column<int>(type: "INT", nullable: false),
                    item_count = table.Column<int>(type: "INT", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.item_id);
                    table.CheckConstraint("CK_PositiveCount", "\"item_count\" > 0");
                    table.ForeignKey(
                        name: "fk_order_items_purchase_orders_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_items_purchase_order_id",
                table: "order_items",
                column: "purchase_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "purchase_orders");

            migrationBuilder.DropSequence(
                name: "OrderNumberSequence");
        }
    }
}
