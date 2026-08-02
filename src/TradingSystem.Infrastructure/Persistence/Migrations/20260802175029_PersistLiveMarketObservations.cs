using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistLiveMarketObservations : Migration
    {
        private static readonly string[] ObservationIndexColumns =
            ["InstrumentId", "SourceTimestampUtc", "Source"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_observations",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    VolumeDelta = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterest = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_observations_InstrumentId_SourceTimestampUtc_Source",
                schema: "trading",
                table: "market_observations",
                columns: ObservationIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_observations_ReceivedAtUtc",
                schema: "trading",
                table: "market_observations",
                column: "ReceivedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_observations",
                schema: "trading");
        }
    }
}
