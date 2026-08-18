using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class PolicyApprovals : Migration
{
    public const string MigrationId = "20260731234000_PolicyApprovals";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            CreatePostgresSchema(migrationBuilder);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            CreateSqliteSchema(migrationBuilder);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ExternalActions");
        migrationBuilder.DropTable(name: "Approvals");
    }

    private static void CreatePostgresSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "Approvals" (
                "ExecutionId" uuid NOT NULL,
                "ActionId" character varying(64) NOT NULL,
                "TenantId" character varying(200) NOT NULL,
                "Status" character varying(40) NOT NULL,
                "Capability" character varying(200) NOT NULL,
                "SideEffectClass" character varying(40) NOT NULL,
                "RequiredPermissionScopesJson" text NOT NULL,
                "RequestedByActorId" character varying(200) NOT NULL,
                "DecidedByActorId" character varying(200) NULL,
                "InputReference" character varying(64) NOT NULL,
                "IdempotencyReference" character varying(64) NOT NULL,
                "CreatedAtUnixMilliseconds" bigint NOT NULL,
                "DecidedAtUnixMilliseconds" bigint NULL,
                "Reason" text NULL,
                CONSTRAINT "PK_Approvals" PRIMARY KEY ("ExecutionId", "ActionId"),
                CONSTRAINT "FK_Approvals_Executions_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_Approvals_TenantId_Status"
                ON "Approvals" ("TenantId", "Status");

            CREATE TABLE IF NOT EXISTS "ExternalActions" (
                "TenantId" character varying(200) NOT NULL,
                "IdempotencyReference" character varying(64) NOT NULL,
                "ExecutionId" uuid NOT NULL,
                "ActionId" character varying(64) NOT NULL,
                "Capability" character varying(200) NOT NULL,
                "InputReference" character varying(64) NOT NULL,
                "Status" character varying(40) NOT NULL,
                "ResultJson" text NULL,
                "ErrorJson" text NULL,
                "CreatedAtUnixMilliseconds" bigint NOT NULL,
                "UpdatedAtUnixMilliseconds" bigint NOT NULL,
                CONSTRAINT "PK_ExternalActions" PRIMARY KEY ("TenantId", "IdempotencyReference"),
                CONSTRAINT "FK_ExternalActions_Executions_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ExternalActions_ExecutionId_ActionId"
                ON "ExternalActions" ("ExecutionId", "ActionId");
            CREATE INDEX IF NOT EXISTS "IX_ExternalActions_Status"
                ON "ExternalActions" ("Status");
            """);
    }

    private static void CreateSqliteSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "Approvals" (
                "ExecutionId" TEXT NOT NULL,
                "ActionId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Capability" TEXT NOT NULL,
                "SideEffectClass" TEXT NOT NULL,
                "RequiredPermissionScopesJson" TEXT NOT NULL,
                "RequestedByActorId" TEXT NOT NULL,
                "DecidedByActorId" TEXT NULL,
                "InputReference" TEXT NOT NULL,
                "IdempotencyReference" TEXT NOT NULL,
                "CreatedAtUnixMilliseconds" INTEGER NOT NULL,
                "DecidedAtUnixMilliseconds" INTEGER NULL,
                "Reason" TEXT NULL,
                CONSTRAINT "PK_Approvals" PRIMARY KEY ("ExecutionId", "ActionId"),
                CONSTRAINT "FK_Approvals_Executions_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_Approvals_TenantId_Status"
                ON "Approvals" ("TenantId", "Status");

            CREATE TABLE IF NOT EXISTS "ExternalActions" (
                "TenantId" TEXT NOT NULL,
                "IdempotencyReference" TEXT NOT NULL,
                "ExecutionId" TEXT NOT NULL,
                "ActionId" TEXT NOT NULL,
                "Capability" TEXT NOT NULL,
                "InputReference" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ResultJson" TEXT NULL,
                "ErrorJson" TEXT NULL,
                "CreatedAtUnixMilliseconds" INTEGER NOT NULL,
                "UpdatedAtUnixMilliseconds" INTEGER NOT NULL,
                CONSTRAINT "PK_ExternalActions" PRIMARY KEY ("TenantId", "IdempotencyReference"),
                CONSTRAINT "FK_ExternalActions_Executions_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ExternalActions_ExecutionId_ActionId"
                ON "ExternalActions" ("ExecutionId", "ActionId");
            CREATE INDEX IF NOT EXISTS "IX_ExternalActions_Status"
                ON "ExternalActions" ("Status");
            """);
    }
}
