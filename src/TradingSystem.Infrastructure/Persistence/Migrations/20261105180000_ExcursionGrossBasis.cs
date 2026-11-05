using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261105180000_ExcursionGrossBasis")]
public sealed class ExcursionGrossBasis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>(name: "ExcursionBasisVersion", schema: "trading",
            table: "market_paper_positions", type: "integer", nullable: false, defaultValue: 0);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "ExcursionBasisVersion", schema: "trading",
            table: "market_paper_positions");
}
