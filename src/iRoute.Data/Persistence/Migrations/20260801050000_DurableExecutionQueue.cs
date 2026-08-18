using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class DurableExecutionWork : Migration
{
    public const string MigrationId = "20260801050000_DurableExecutionQueue";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "ExecutionWorkItems" (
                    "ExecutionId" uuid NOT NULL,
                    "State" character varying(40) NOT NULL,
                    "AvailableAtUnixMilliseconds" bigint NOT NULL,
                    "DeliveryAttempt" integer NOT NULL,
                    "LeaseOwner" character varying(200),
                    "LeaseToken" uuid,
                    "LeaseExpiresAtUnixMilliseconds" bigint,
                    "HeartbeatAtUnixMilliseconds" bigint,
                    "CompletedAtUnixMilliseconds" bigint,
                    CONSTRAINT "PK_ExecutionWorkItems" PRIMARY KEY ("ExecutionId"),
                    CONSTRAINT "FK_ExecutionWorkItems_Executions_ExecutionId"
                        FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
                );
                CREATE INDEX "IX_ExecutionWorkItems_State_AvailableAtUnixMilliseconds"
                    ON "ExecutionWorkItems" ("State", "AvailableAtUnixMilliseconds");
                CREATE INDEX "IX_ExecutionWorkItems_LeaseExpiresAtUnixMilliseconds"
                    ON "ExecutionWorkItems" ("LeaseExpiresAtUnixMilliseconds");
                """);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "ExecutionWorkItems" (
                    "ExecutionId" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "AvailableAtUnixMilliseconds" INTEGER NOT NULL,
                    "DeliveryAttempt" INTEGER NOT NULL,
                    "LeaseOwner" TEXT,
                    "LeaseToken" TEXT,
                    "LeaseExpiresAtUnixMilliseconds" INTEGER,
                    "HeartbeatAtUnixMilliseconds" INTEGER,
                    "CompletedAtUnixMilliseconds" INTEGER,
                    CONSTRAINT "PK_ExecutionWorkItems" PRIMARY KEY ("ExecutionId"),
                    CONSTRAINT "FK_ExecutionWorkItems_Executions_ExecutionId"
                        FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
                );
                CREATE INDEX "IX_ExecutionWorkItems_State_AvailableAtUnixMilliseconds"
                    ON "ExecutionWorkItems" ("State", "AvailableAtUnixMilliseconds");
                CREATE INDEX "IX_ExecutionWorkItems_LeaseExpiresAtUnixMilliseconds"
                    ON "ExecutionWorkItems" ("LeaseExpiresAtUnixMilliseconds");
                """);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ExecutionWorkItems");
}
