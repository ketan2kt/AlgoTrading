using System;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861 // EF migration scaffolding uses inline column arrays.

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalInstrumentMasterSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "historical_instrument_master_snapshots",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractCount = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historical_instrument_master_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_historical_instrument_master_snapshots_SessionDate_Captured~",
                schema: "trading",
                table: "historical_instrument_master_snapshots",
                columns: new[] { "SessionDate", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_historical_instrument_master_snapshots_SessionDate_Source_F~",
                schema: "trading",
                table: "historical_instrument_master_snapshots",
                columns: new[] { "SessionDate", "Source", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "historical_instrument_master_snapshots",
                schema: "trading");
        }
    }
}
