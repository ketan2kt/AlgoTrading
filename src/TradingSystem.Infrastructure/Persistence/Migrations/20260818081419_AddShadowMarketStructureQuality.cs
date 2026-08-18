using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShadowMarketStructureQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShadowEvidenceJson",
                schema: "trading",
                table: "strategy_evaluations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowStructureState",
                schema: "trading",
                table: "strategy_evaluations",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShadowTrendQuality",
                schema: "trading",
                table: "strategy_evaluations",
                type: "numeric(7,6)",
                precision: 7,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShadowWouldPermit",
                schema: "trading",
                table: "strategy_evaluations",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShadowEvidenceJson",
                schema: "trading",
                table: "strategy_evaluations");

            migrationBuilder.DropColumn(
                name: "ShadowStructureState",
                schema: "trading",
                table: "strategy_evaluations");

            migrationBuilder.DropColumn(
                name: "ShadowTrendQuality",
                schema: "trading",
                table: "strategy_evaluations");

            migrationBuilder.DropColumn(
                name: "ShadowWouldPermit",
                schema: "trading",
                table: "strategy_evaluations");
        }
    }
}
