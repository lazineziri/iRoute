using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Infrastructure.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class GatewayCircuitBreaker : Migration
{
    public const string MigrationId = "20260802010000_GatewayCircuitBreaker";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "GatewayCircuits" (
                    "DeploymentId" character varying(200) NOT NULL,
                    "State" character varying(40) NOT NULL,
                    "ConsecutiveFailures" integer NOT NULL,
                    "OpenCount" integer NOT NULL,
                    "OpenedAtUnixMilliseconds" bigint,
                    "NextProbeAtUnixMilliseconds" bigint,
                    "ProbeOwner" character varying(200),
                    "ProbeToken" uuid,
                    "ProbeLeaseExpiresAtUnixMilliseconds" bigint,
                    "LastFailureClass" character varying(40),
                    "LastFailureAtUnixMilliseconds" bigint,
                    "UpdatedAtUnixMilliseconds" bigint NOT NULL,
                    CONSTRAINT "PK_GatewayCircuits" PRIMARY KEY ("DeploymentId")
                );
                CREATE INDEX "IX_GatewayCircuits_State_NextProbeAtUnixMilliseconds"
                    ON "GatewayCircuits" ("State", "NextProbeAtUnixMilliseconds");
                CREATE INDEX "IX_GatewayCircuits_ProbeLeaseExpiresAtUnixMilliseconds"
                    ON "GatewayCircuits" ("ProbeLeaseExpiresAtUnixMilliseconds");
                """);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "GatewayCircuits" (
                    "DeploymentId" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "ConsecutiveFailures" INTEGER NOT NULL,
                    "OpenCount" INTEGER NOT NULL,
                    "OpenedAtUnixMilliseconds" INTEGER,
                    "NextProbeAtUnixMilliseconds" INTEGER,
                    "ProbeOwner" TEXT,
                    "ProbeToken" TEXT,
                    "ProbeLeaseExpiresAtUnixMilliseconds" INTEGER,
                    "LastFailureClass" TEXT,
                    "LastFailureAtUnixMilliseconds" INTEGER,
                    "UpdatedAtUnixMilliseconds" INTEGER NOT NULL,
                    CONSTRAINT "PK_GatewayCircuits" PRIMARY KEY ("DeploymentId")
                );
                CREATE INDEX "IX_GatewayCircuits_State_NextProbeAtUnixMilliseconds"
                    ON "GatewayCircuits" ("State", "NextProbeAtUnixMilliseconds");
                CREATE INDEX "IX_GatewayCircuits_ProbeLeaseExpiresAtUnixMilliseconds"
                    ON "GatewayCircuits" ("ProbeLeaseExpiresAtUnixMilliseconds");
                """);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "GatewayCircuits");
}
