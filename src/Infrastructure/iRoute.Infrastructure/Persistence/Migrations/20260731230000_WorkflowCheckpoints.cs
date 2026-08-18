using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Infrastructure.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class WorkflowCheckpoints : Migration
{
    public const string MigrationId = "20260731230000_WorkflowCheckpoints";

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
        migrationBuilder.DropTable(name: "WorkflowSteps");
        migrationBuilder.DropTable(name: "WorkflowPlans");
    }

    private static void CreatePostgresSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "WorkflowPlans" (
                "ExecutionId" uuid NOT NULL,
                "RequestJson" text NOT NULL,
                "PlanJson" text NOT NULL,
                "CreatedAtUnixMilliseconds" bigint NOT NULL,
                "UpdatedAtUnixMilliseconds" bigint NOT NULL,
                CONSTRAINT "PK_WorkflowPlans" PRIMARY KEY ("ExecutionId"),
                CONSTRAINT "FK_WorkflowPlans_Executions_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "WorkflowSteps" (
                "ExecutionId" uuid NOT NULL,
                "StepId" character varying(64) NOT NULL,
                "Status" character varying(40) NOT NULL,
                "Attempt" integer NOT NULL,
                "StartedAtUnixMilliseconds" bigint NULL,
                "CompletedAtUnixMilliseconds" bigint NULL,
                "OutputJson" text NULL,
                "ErrorJson" text NULL,
                CONSTRAINT "PK_WorkflowSteps" PRIMARY KEY ("ExecutionId", "StepId"),
                CONSTRAINT "FK_WorkflowSteps_WorkflowPlans_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "WorkflowPlans" ("ExecutionId") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_WorkflowSteps_ExecutionId_Status"
                ON "WorkflowSteps" ("ExecutionId", "Status");
            """);
    }

    private static void CreateSqliteSchema(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "WorkflowPlans" (
                "ExecutionId" TEXT NOT NULL,
                "RequestJson" TEXT NOT NULL,
                "PlanJson" TEXT NOT NULL,
                "CreatedAtUnixMilliseconds" INTEGER NOT NULL,
                "UpdatedAtUnixMilliseconds" INTEGER NOT NULL,
                CONSTRAINT "PK_WorkflowPlans" PRIMARY KEY ("ExecutionId"),
                CONSTRAINT "FK_WorkflowPlans_Executions_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "Executions" ("ExecutionId") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "WorkflowSteps" (
                "ExecutionId" TEXT NOT NULL,
                "StepId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Attempt" INTEGER NOT NULL,
                "StartedAtUnixMilliseconds" INTEGER NULL,
                "CompletedAtUnixMilliseconds" INTEGER NULL,
                "OutputJson" TEXT NULL,
                "ErrorJson" TEXT NULL,
                CONSTRAINT "PK_WorkflowSteps" PRIMARY KEY ("ExecutionId", "StepId"),
                CONSTRAINT "FK_WorkflowSteps_WorkflowPlans_ExecutionId"
                    FOREIGN KEY ("ExecutionId") REFERENCES "WorkflowPlans" ("ExecutionId") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_WorkflowSteps_ExecutionId_Status"
                ON "WorkflowSteps" ("ExecutionId", "Status");
            """);
    }
}
