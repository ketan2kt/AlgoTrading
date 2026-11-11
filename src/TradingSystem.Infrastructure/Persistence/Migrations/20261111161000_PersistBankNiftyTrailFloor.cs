using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261111161000_PersistBankNiftyTrailFloor")]
public sealed class PersistBankNiftyTrailFloor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<decimal>(name: "BankNiftyTrailFloorNet", schema: "trading",
            table: "market_paper_positions", type: "numeric(18,4)", precision: 18, scale: 4, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "BankNiftyTrailFloorNet", schema: "trading", table: "market_paper_positions");
}
