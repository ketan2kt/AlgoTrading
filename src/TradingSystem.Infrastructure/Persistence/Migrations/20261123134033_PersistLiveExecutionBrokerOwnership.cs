using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistLiveExecutionBrokerOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionAccount",
                schema: "trading",
                table: "live_execution_intents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "primary");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionBroker",
                schema: "trading",
                table: "live_execution_intents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Groww");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionAccount",
                schema: "trading",
                table: "live_execution_intents");

            migrationBuilder.DropColumn(
                name: "ExecutionBroker",
                schema: "trading",
                table: "live_execution_intents");
        }
    }
}
