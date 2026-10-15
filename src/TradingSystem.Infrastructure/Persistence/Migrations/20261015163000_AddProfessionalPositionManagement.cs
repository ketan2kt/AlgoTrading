using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261015163000_AddProfessionalPositionManagement")]
public sealed class AddProfessionalPositionManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(name: "CostsPaid", schema: "trading",
            table: "market_paper_positions", type: "numeric(18,4)", nullable: false,
            defaultValue: 0m);
        migrationBuilder.AddColumn<int>(name: "PartialExitQuantity", schema: "trading",
            table: "market_paper_positions", type: "integer", nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<decimal>(name: "PartialExitPrice", schema: "trading",
            table: "market_paper_positions", type: "numeric(18,4)", nullable: true);
        migrationBuilder.AddColumn<int>(name: "RemainingQuantity", schema: "trading",
            table: "market_paper_positions", type: "integer", nullable: false,
            defaultValue: 0);
        migrationBuilder.Sql("""
            UPDATE trading.market_paper_positions
            SET "RemainingQuantity" = CASE WHEN "Status" = 'Active' THEN "Quantity" ELSE 0 END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CostsPaid", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "PartialExitQuantity", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "PartialExitPrice", schema: "trading",
            table: "market_paper_positions");
        migrationBuilder.DropColumn(name: "RemainingQuantity", schema: "trading",
            table: "market_paper_positions");
    }
}
