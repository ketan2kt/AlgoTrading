#pragma warning disable CA1861 // EF migration APIs use inline column arrays.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingSystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradingDbContext))]
[Migration("20261026160000_AddContactInquiries")]
public sealed class AddContactInquiries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "contact_inquiries", schema: "trading",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                Company = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                Service = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                DeliveryStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                DeliveryAttempts = table.Column<int>(type: "integer", nullable: false),
                NextDeliveryAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeliveredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_contact_inquiries", x => x.Id));
        migrationBuilder.CreateIndex("IX_contact_inquiries_DeliveryStatus_NextDeliveryAtUtc", "contact_inquiries",
            new[] { "DeliveryStatus", "NextDeliveryAtUtc" }, schema: "trading");
        migrationBuilder.CreateIndex("IX_contact_inquiries_CreatedAtUtc", "contact_inquiries",
            "CreatedAtUtc", schema: "trading");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "contact_inquiries", schema: "trading");
}
#pragma warning restore CA1861
