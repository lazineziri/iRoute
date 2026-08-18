using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class InitialRuntimeStorage : Migration
{
    public const string MigrationId = "20260731190000_InitialRuntimeStorage";

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
        migrationBuilder.DropTable(name: "ExecutionEvents");
        migrationBuilder.DropTable(name: "Artifacts");
        migrationBuilder.DropTable(name: "Executions");
    }

    private static void CreatePostgresSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "Executions" (
                "ExecutionId" uuid NOT NULL,
                "TenantId" character varying(200) NOT NULL,
                "ActorId" character varying(200) NOT NULL,
                "ProjectId" character varying(200) NULL,
                "TaskType" character varying(120) NOT NULL,
                "TaskDefinitionVersion" integer NULL,
                "Status" character varying(40) NOT NULL,
                "CreatedAtUnixMilliseconds" bigint NOT NULL,
                "UpdatedAtUnixMilliseconds" bigint NOT NULL,
                "CancellationRequestedAtUnixMilliseconds" bigint NULL,
                "IdempotencyKey" character varying(200) NULL,
                "OutcomeJson" text NULL,
                "ErrorJson" text NULL,
                CONSTRAINT "PK_Executions" PRIMARY KEY ("ExecutionId")
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Executions_TenantId_IdempotencyKey"
                ON "Executions" ("TenantId", "IdempotencyKey");

            CREATE TABLE IF NOT EXISTS "ExecutionEvents" (
                "ExecutionId" uuid NOT NULL,
                "Sequence" bigint NOT NULL,
                "EventType" character varying(120) NOT NULL,
                "OccurredAtUnixMilliseconds" bigint NOT NULL,
                "DataJson" text NOT NULL,
                CONSTRAINT "PK_ExecutionEvents" PRIMARY KEY ("ExecutionId", "Sequence")
            );
            CREATE INDEX IF NOT EXISTS "IX_ExecutionEvents_ExecutionId_OccurredAtUnixMilliseconds"
                ON "ExecutionEvents" ("ExecutionId", "OccurredAtUnixMilliseconds");

            CREATE TABLE IF NOT EXISTS "Artifacts" (
                "ArtifactId" uuid NOT NULL,
                "TenantId" character varying(200) NOT NULL,
                "ProjectId" character varying(200) NOT NULL,
                "TaskType" character varying(120) NOT NULL,
                "TaskDefinitionVersion" integer NOT NULL,
                "ArtifactType" character varying(120) NOT NULL,
                "Version" integer NOT NULL,
                "InputHash" character varying(64) NOT NULL,
                "ContentHash" character varying(64) NOT NULL,
                "ContentJson" text NOT NULL,
                "EvidenceJson" text NOT NULL,
                "CreatedAtUnixMilliseconds" bigint NOT NULL,
                "ExpiresAtUnixMilliseconds" bigint NULL,
                "IsActive" boolean NOT NULL,
                CONSTRAINT "PK_Artifacts" PRIMARY KEY ("ArtifactId")
            );
            CREATE INDEX IF NOT EXISTS "IX_Artifacts_TenantId_ProjectId_TaskType_TaskDefinitionVersion_InputHash_IsActive"
                ON "Artifacts" ("TenantId", "ProjectId", "TaskType", "TaskDefinitionVersion", "InputHash", "IsActive");
            """);
    }

    private static void CreateSqliteSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "Executions" (
                "ExecutionId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "ActorId" TEXT NOT NULL,
                "ProjectId" TEXT NULL,
                "TaskType" TEXT NOT NULL,
                "TaskDefinitionVersion" INTEGER NULL,
                "Status" TEXT NOT NULL,
                "CreatedAtUnixMilliseconds" INTEGER NOT NULL,
                "UpdatedAtUnixMilliseconds" INTEGER NOT NULL,
                "CancellationRequestedAtUnixMilliseconds" INTEGER NULL,
                "IdempotencyKey" TEXT NULL,
                "OutcomeJson" TEXT NULL,
                "ErrorJson" TEXT NULL,
                CONSTRAINT "PK_Executions" PRIMARY KEY ("ExecutionId")
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Executions_TenantId_IdempotencyKey"
                ON "Executions" ("TenantId", "IdempotencyKey");

            CREATE TABLE IF NOT EXISTS "ExecutionEvents" (
                "ExecutionId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "OccurredAtUnixMilliseconds" INTEGER NOT NULL,
                "DataJson" TEXT NOT NULL,
                CONSTRAINT "PK_ExecutionEvents" PRIMARY KEY ("ExecutionId", "Sequence")
            );
            CREATE INDEX IF NOT EXISTS "IX_ExecutionEvents_ExecutionId_OccurredAtUnixMilliseconds"
                ON "ExecutionEvents" ("ExecutionId", "OccurredAtUnixMilliseconds");

            CREATE TABLE IF NOT EXISTS "Artifacts" (
                "ArtifactId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "ProjectId" TEXT NOT NULL,
                "TaskType" TEXT NOT NULL,
                "TaskDefinitionVersion" INTEGER NOT NULL,
                "ArtifactType" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "InputHash" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "ContentJson" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "CreatedAtUnixMilliseconds" INTEGER NOT NULL,
                "ExpiresAtUnixMilliseconds" INTEGER NULL,
                "IsActive" INTEGER NOT NULL,
                CONSTRAINT "PK_Artifacts" PRIMARY KEY ("ArtifactId")
            );
            CREATE INDEX IF NOT EXISTS "IX_Artifacts_TenantId_ProjectId_TaskType_TaskDefinitionVersion_InputHash_IsActive"
                ON "Artifacts" ("TenantId", "ProjectId", "TaskType", "TaskDefinitionVersion", "InputHash", "IsActive");
            """);
    }
}
