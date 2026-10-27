using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackLiveMirroredActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MirroredAddOnQuantity",
                schema: "trading",
                table: "live_execution_intents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MirroredPartialExitQuantity",
                schema: "trading",
                table: "live_execution_intents",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MirroredAddOnQuantity",
                schema: "trading",
                table: "live_execution_intents");

            migrationBuilder.DropColumn(
                name: "MirroredPartialExitQuantity",
                schema: "trading",
                table: "live_execution_intents");
        }
    }
}
