using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurablePaperBrokerJournal : Migration
    {
        private static readonly string[] ClientReferenceSequenceColumns =
            ["ClientReference", "Sequence"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_broker_events",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ClientReference = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_broker_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paper_broker_events_ClientReference_Sequence",
                schema: "trading",
                table: "paper_broker_events",
                columns: ClientReferenceSequenceColumns);

            migrationBuilder.CreateIndex(
                name: "IX_paper_broker_events_Sequence",
                schema: "trading",
                table: "paper_broker_events",
                column: "Sequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_broker_events",
                schema: "trading");
        }
    }
}
