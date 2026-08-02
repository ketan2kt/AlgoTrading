using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectGrowwDailyAccessToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "broker_access_token_secrets",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProtectedValue = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_access_token_secrets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_broker_access_token_secrets_Provider",
                schema: "trading",
                table: "broker_access_token_secrets",
                column: "Provider",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_access_token_secrets",
                schema: "trading");
        }
    }
}
