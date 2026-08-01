using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Infrastructure.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class LifecycleCleanup : Migration
{
    public const string MigrationId = "20260801030000_LifecycleCleanup";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "LifecycleArchives" (
                    "ResourceKind" character varying(40) NOT NULL,
                    "ResourceId" uuid NOT NULL,
                    "TenantId" character varying(200) NOT NULL,
                    "ContentHash" character varying(64) NOT NULL,
                    "PayloadJson" text NOT NULL,
                    "ArchivedAtUnixMilliseconds" bigint NOT NULL,
                    CONSTRAINT "PK_LifecycleArchives" PRIMARY KEY ("TenantId", "ResourceKind", "ResourceId")
                );
                CREATE INDEX "IX_LifecycleArchives_TenantId_ArchivedAtUnixMilliseconds"
                    ON "LifecycleArchives" ("TenantId", "ArchivedAtUnixMilliseconds");
                """);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "LifecycleArchives" (
                    "ResourceKind" TEXT NOT NULL,
                    "ResourceId" TEXT NOT NULL,
                    "TenantId" TEXT NOT NULL,
                    "ContentHash" TEXT NOT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "ArchivedAtUnixMilliseconds" INTEGER NOT NULL,
                    CONSTRAINT "PK_LifecycleArchives" PRIMARY KEY ("TenantId", "ResourceKind", "ResourceId")
                );
                CREATE INDEX "IX_LifecycleArchives_TenantId_ArchivedAtUnixMilliseconds"
                    ON "LifecycleArchives" ("TenantId", "ArchivedAtUnixMilliseconds");
                """);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LifecycleArchives");
}
