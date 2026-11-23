using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261123123000_PersistExternalLiveExitReconciliation")]
public sealed class PersistExternalLiveExitReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "ExternalExitQuantity", schema: "trading", table: "live_execution_intents",
            type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "ExternalExitEvidenceJson", schema: "trading", table: "live_execution_intents",
            type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "ExternalReconciliationPending", schema: "trading", table: "live_execution_intents",
            type: "boolean", nullable: false, defaultValue: false);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ExternalExitQuantity", schema: "trading", table: "live_execution_intents");
        migrationBuilder.DropColumn(name: "ExternalExitEvidenceJson", schema: "trading", table: "live_execution_intents");
        migrationBuilder.DropColumn(name: "ExternalReconciliationPending", schema: "trading", table: "live_execution_intents");
    }
}
