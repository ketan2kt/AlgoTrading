#pragma warning disable CA1861 // EF migration APIs use inline column arrays.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261026170000_AddPreSessionBriefSnapshots")]
public sealed class AddPreSessionBriefSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pre_session_brief_snapshots", schema: "trading",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Market = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_pre_session_brief_snapshots", x => x.Id));
        migrationBuilder.CreateIndex("IX_pre_session_brief_snapshots_SessionDate_Market_Stage",
            "pre_session_brief_snapshots", new[] { "SessionDate", "Market", "Stage" },
            schema: "trading", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "pre_session_brief_snapshots", schema: "trading");
}
#pragma warning restore CA1861
