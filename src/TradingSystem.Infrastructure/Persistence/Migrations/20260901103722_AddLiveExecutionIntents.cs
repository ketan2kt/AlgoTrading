using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveExecutionIntents : Migration
    {
        private static readonly string[] SourceIndexColumns = ["SourceType", "SourceId"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "live_execution_intents",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Market = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    RequestedEntry = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Target = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ClientReference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BrokerOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProtectionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FilledQuantity = table.Column<int>(type: "integer", nullable: false),
                    AverageFillPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProtectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_execution_intents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_execution_intents_ClientReference",
                schema: "trading",
                table: "live_execution_intents",
                column: "ClientReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_live_execution_intents_SourceType_SourceId",
                schema: "trading",
                table: "live_execution_intents",
                columns: SourceIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_live_execution_intents_Status",
                schema: "trading",
                table: "live_execution_intents",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_execution_intents",
                schema: "trading");
        }
    }
}
