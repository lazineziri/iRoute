using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class TenantQuotas : Migration
{
    public const string MigrationId = "20261001020000_TenantQuotas";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("TenantQuotaAccounts", columns: table => new
        { TenantId = table.Column<string>(maxLength: 200, nullable: false), Revision = table.Column<long>(nullable: false) },
            constraints: table => table.PrimaryKey("PK_TenantQuotaAccounts", item => item.TenantId));
        migrationBuilder.CreateTable("TenantDispatch", columns: table => new
        { TenantId = table.Column<string>(maxLength: 200, nullable: false), LastDispatch = table.Column<long>(nullable: false) },
            constraints: table => table.PrimaryKey("PK_TenantDispatch", item => item.TenantId));
        migrationBuilder.CreateTable("TenantQuotaReservations", columns: table => new
        {
            ReservationId = table.Column<Guid>(nullable: false),
            TenantId = table.Column<string>(maxLength: 200, nullable: false),
            WindowStartUnixMilliseconds = table.Column<long>(nullable: false),
            WindowEndUnixMilliseconds = table.Column<long>(nullable: false),
            LeaseExpiresAtUnixMilliseconds = table.Column<long>(nullable: false),
            ChargedTokens = table.Column<long>(nullable: false),
            ChargedCostMicroUnits = table.Column<long>(nullable: true),
            UsageUnknown = table.Column<bool>(nullable: false),
            CompletedAtUnixMilliseconds = table.Column<long>(nullable: true)
        }, constraints: table => table.PrimaryKey("PK_TenantQuotaReservations", item => item.ReservationId));
        migrationBuilder.CreateIndex("IX_TenantQuotaReservations_TenantId_WindowStartUnixMilliseconds", "TenantQuotaReservations",
            ["TenantId", "WindowStartUnixMilliseconds"]);
        migrationBuilder.CreateIndex("IX_TenantQuotaReservations_TenantId_LeaseExpiresAtUnixMilliseconds", "TenantQuotaReservations",
            ["TenantId", "LeaseExpiresAtUnixMilliseconds"]);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("TenantQuotaReservations");
        migrationBuilder.DropTable("TenantQuotaAccounts");
        migrationBuilder.DropTable("TenantDispatch");
    }
}
