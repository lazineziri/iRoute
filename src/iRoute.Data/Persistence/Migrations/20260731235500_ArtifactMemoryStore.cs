using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class ArtifactMemoryStore : Migration
{
    public const string MigrationId = "20260731235500_ArtifactMemoryStore";

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
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "DependencyEdges";
                DROP TABLE IF EXISTS "MemoryRecords";
                DROP INDEX IF EXISTS "UX_Artifacts_ActiveLineage";
                DROP INDEX IF EXISTS "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_IsActive";
                DROP INDEX IF EXISTS "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_Version";
                ALTER TABLE "Artifacts" DROP COLUMN IF EXISTS "InvalidationReason";
                ALTER TABLE "Artifacts" DROP COLUMN IF EXISTS "InvalidatedAtUnixMilliseconds";
                ALTER TABLE "Artifacts" DROP COLUMN IF EXISTS "SupersededByArtifactId";
                ALTER TABLE "Artifacts" DROP COLUMN IF EXISTS "SupersedesArtifactId";
                ALTER TABLE "Artifacts" DROP COLUMN IF EXISTS "LifecycleStatus";
                ALTER TABLE "Artifacts" DROP COLUMN IF EXISTS "LogicalKey";
                """);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "DependencyEdges";
                DROP TABLE IF EXISTS "MemoryRecords";
                DROP INDEX IF EXISTS "UX_Artifacts_ActiveLineage";
                DROP INDEX IF EXISTS "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_IsActive";
                DROP INDEX IF EXISTS "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_Version";
                ALTER TABLE "Artifacts" DROP COLUMN "InvalidationReason";
                ALTER TABLE "Artifacts" DROP COLUMN "InvalidatedAtUnixMilliseconds";
                ALTER TABLE "Artifacts" DROP COLUMN "SupersededByArtifactId";
                ALTER TABLE "Artifacts" DROP COLUMN "SupersedesArtifactId";
                ALTER TABLE "Artifacts" DROP COLUMN "LifecycleStatus";
                ALTER TABLE "Artifacts" DROP COLUMN "LogicalKey";
                """);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    private static void CreatePostgresSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE "Artifacts" ADD COLUMN "LogicalKey" character varying(200) NOT NULL DEFAULT '';
            ALTER TABLE "Artifacts" ADD COLUMN "LifecycleStatus" character varying(40) NOT NULL DEFAULT 'Active';
            ALTER TABLE "Artifacts" ADD COLUMN "SupersedesArtifactId" uuid NULL;
            ALTER TABLE "Artifacts" ADD COLUMN "SupersededByArtifactId" uuid NULL;
            ALTER TABLE "Artifacts" ADD COLUMN "InvalidatedAtUnixMilliseconds" bigint NULL;
            ALTER TABLE "Artifacts" ADD COLUMN "InvalidationReason" text NULL;
            UPDATE "Artifacts" SET "LogicalKey" = "TaskType" WHERE "LogicalKey" = '';
            WITH lineage AS (
                SELECT
                    "ArtifactId",
                    ROW_NUMBER() OVER (
                        PARTITION BY "TenantId", "ProjectId", "ArtifactType", "LogicalKey"
                        ORDER BY "Version" DESC, "ArtifactId" DESC) AS "ActiveRank",
                    LAG("ArtifactId") OVER (
                        PARTITION BY "TenantId", "ProjectId", "ArtifactType", "LogicalKey"
                        ORDER BY "Version", "ArtifactId") AS "Supersedes",
                    LEAD("ArtifactId") OVER (
                        PARTITION BY "TenantId", "ProjectId", "ArtifactType", "LogicalKey"
                        ORDER BY "Version", "ArtifactId") AS "SupersededBy"
                FROM "Artifacts"
            )
            UPDATE "Artifacts" AS artifact
            SET
                "IsActive" = lineage."ActiveRank" = 1,
                "LifecycleStatus" = CASE WHEN lineage."ActiveRank" = 1 THEN 'Active' ELSE 'Superseded' END,
                "SupersedesArtifactId" = lineage."Supersedes",
                "SupersededByArtifactId" = lineage."SupersededBy"
            FROM lineage
            WHERE artifact."ArtifactId" = lineage."ArtifactId";
            CREATE UNIQUE INDEX "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_Version"
                ON "Artifacts" ("TenantId", "ProjectId", "ArtifactType", "LogicalKey", "Version");
            CREATE INDEX "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_IsActive"
                ON "Artifacts" ("TenantId", "ProjectId", "ArtifactType", "LogicalKey", "IsActive");
            CREATE UNIQUE INDEX "UX_Artifacts_ActiveLineage"
                ON "Artifacts" ("TenantId", "ProjectId", "ArtifactType", "LogicalKey")
                WHERE "IsActive" AND "LifecycleStatus" = 'Active';

            CREATE TABLE "MemoryRecords" (
                "MemoryId" uuid NOT NULL,
                "TenantId" character varying(200) NOT NULL,
                "ProjectId" character varying(200) NOT NULL,
                "Kind" character varying(40) NOT NULL,
                "Key" character varying(200) NOT NULL,
                "Version" integer NOT NULL,
                "ValueJson" text NOT NULL,
                "ContentHash" character varying(64) NOT NULL,
                "LifecycleStatus" character varying(40) NOT NULL,
                "EvidenceJson" text NOT NULL,
                "CreatedAtUnixMilliseconds" bigint NOT NULL,
                "ExpiresAtUnixMilliseconds" bigint NULL,
                "SupersedesMemoryId" uuid NULL,
                "SupersededByMemoryId" uuid NULL,
                "InvalidatedAtUnixMilliseconds" bigint NULL,
                "InvalidationReason" text NULL,
                CONSTRAINT "PK_MemoryRecords" PRIMARY KEY ("MemoryId")
            );
            CREATE UNIQUE INDEX "IX_MemoryRecords_TenantId_ProjectId_Kind_Key_Version"
                ON "MemoryRecords" ("TenantId", "ProjectId", "Kind", "Key", "Version");
            CREATE INDEX "IX_MemoryRecords_TenantId_ProjectId_Kind_Key_LifecycleStatus"
                ON "MemoryRecords" ("TenantId", "ProjectId", "Kind", "Key", "LifecycleStatus");
            CREATE UNIQUE INDEX "UX_MemoryRecords_ActiveLineage"
                ON "MemoryRecords" ("TenantId", "ProjectId", "Kind", "Key")
                WHERE "LifecycleStatus" = 'Active';

            CREATE TABLE "DependencyEdges" (
                "TenantId" character varying(200) NOT NULL,
                "SourceKind" character varying(40) NOT NULL,
                "SourceId" uuid NOT NULL,
                "TargetKind" character varying(120) NOT NULL,
                "TargetReference" character varying(500) NOT NULL,
                "TargetContentHash" character varying(64) NULL,
                CONSTRAINT "PK_DependencyEdges"
                    PRIMARY KEY ("SourceKind", "SourceId", "TargetKind", "TargetReference")
            );
            CREATE INDEX "IX_DependencyEdges_TenantId_TargetKind_TargetReference"
                ON "DependencyEdges" ("TenantId", "TargetKind", "TargetReference");
            """);
    }

    private static void CreateSqliteSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE "Artifacts" ADD COLUMN "LogicalKey" TEXT NOT NULL DEFAULT '';
            ALTER TABLE "Artifacts" ADD COLUMN "LifecycleStatus" TEXT NOT NULL DEFAULT 'Active';
            ALTER TABLE "Artifacts" ADD COLUMN "SupersedesArtifactId" TEXT NULL;
            ALTER TABLE "Artifacts" ADD COLUMN "SupersededByArtifactId" TEXT NULL;
            ALTER TABLE "Artifacts" ADD COLUMN "InvalidatedAtUnixMilliseconds" INTEGER NULL;
            ALTER TABLE "Artifacts" ADD COLUMN "InvalidationReason" TEXT NULL;
            UPDATE "Artifacts" SET "LogicalKey" = "TaskType" WHERE "LogicalKey" = '';
            WITH lineage AS (
                SELECT
                    "ArtifactId",
                    ROW_NUMBER() OVER (
                        PARTITION BY "TenantId", "ProjectId", "ArtifactType", "LogicalKey"
                        ORDER BY "Version" DESC, "ArtifactId" DESC) AS "ActiveRank",
                    LAG("ArtifactId") OVER (
                        PARTITION BY "TenantId", "ProjectId", "ArtifactType", "LogicalKey"
                        ORDER BY "Version", "ArtifactId") AS "Supersedes",
                    LEAD("ArtifactId") OVER (
                        PARTITION BY "TenantId", "ProjectId", "ArtifactType", "LogicalKey"
                        ORDER BY "Version", "ArtifactId") AS "SupersededBy"
                FROM "Artifacts"
            )
            UPDATE "Artifacts"
            SET
                "IsActive" = CASE
                    WHEN (SELECT "ActiveRank" FROM lineage WHERE lineage."ArtifactId" = "Artifacts"."ArtifactId") = 1
                    THEN 1 ELSE 0 END,
                "LifecycleStatus" = CASE
                    WHEN (SELECT "ActiveRank" FROM lineage WHERE lineage."ArtifactId" = "Artifacts"."ArtifactId") = 1
                    THEN 'Active' ELSE 'Superseded' END,
                "SupersedesArtifactId" = (
                    SELECT "Supersedes" FROM lineage WHERE lineage."ArtifactId" = "Artifacts"."ArtifactId"),
                "SupersededByArtifactId" = (
                    SELECT "SupersededBy" FROM lineage WHERE lineage."ArtifactId" = "Artifacts"."ArtifactId")
            WHERE "ArtifactId" IN (SELECT "ArtifactId" FROM lineage);
            CREATE UNIQUE INDEX "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_Version"
                ON "Artifacts" ("TenantId", "ProjectId", "ArtifactType", "LogicalKey", "Version");
            CREATE INDEX "IX_Artifacts_TenantId_ProjectId_ArtifactType_LogicalKey_IsActive"
                ON "Artifacts" ("TenantId", "ProjectId", "ArtifactType", "LogicalKey", "IsActive");
            CREATE UNIQUE INDEX "UX_Artifacts_ActiveLineage"
                ON "Artifacts" ("TenantId", "ProjectId", "ArtifactType", "LogicalKey")
                WHERE "IsActive" = 1 AND "LifecycleStatus" = 'Active';

            CREATE TABLE "MemoryRecords" (
                "MemoryId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "ProjectId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Key" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "ValueJson" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "LifecycleStatus" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "CreatedAtUnixMilliseconds" INTEGER NOT NULL,
                "ExpiresAtUnixMilliseconds" INTEGER NULL,
                "SupersedesMemoryId" TEXT NULL,
                "SupersededByMemoryId" TEXT NULL,
                "InvalidatedAtUnixMilliseconds" INTEGER NULL,
                "InvalidationReason" TEXT NULL,
                CONSTRAINT "PK_MemoryRecords" PRIMARY KEY ("MemoryId")
            );
            CREATE UNIQUE INDEX "IX_MemoryRecords_TenantId_ProjectId_Kind_Key_Version"
                ON "MemoryRecords" ("TenantId", "ProjectId", "Kind", "Key", "Version");
            CREATE INDEX "IX_MemoryRecords_TenantId_ProjectId_Kind_Key_LifecycleStatus"
                ON "MemoryRecords" ("TenantId", "ProjectId", "Kind", "Key", "LifecycleStatus");
            CREATE UNIQUE INDEX "UX_MemoryRecords_ActiveLineage"
                ON "MemoryRecords" ("TenantId", "ProjectId", "Kind", "Key")
                WHERE "LifecycleStatus" = 'Active';

            CREATE TABLE "DependencyEdges" (
                "TenantId" TEXT NOT NULL,
                "SourceKind" TEXT NOT NULL,
                "SourceId" TEXT NOT NULL,
                "TargetKind" TEXT NOT NULL,
                "TargetReference" TEXT NOT NULL,
                "TargetContentHash" TEXT NULL,
                CONSTRAINT "PK_DependencyEdges"
                    PRIMARY KEY ("SourceKind", "SourceId", "TargetKind", "TargetReference")
            );
            CREATE INDEX "IX_DependencyEdges_TenantId_TargetKind_TargetReference"
                ON "DependencyEdges" ("TenantId", "TargetKind", "TargetReference");
            """);
    }
}
