using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class IdempotencyFingerprint : Migration
{
    public const string MigrationId = "20260802120000_IdempotencyFingerprint";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Nullable with no backfill: executions recorded before this migration have no fingerprint,
        // and a replay of one of those keys is accepted as a retry rather than reported as a
        // conflict, which is the pre-existing behaviour.
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputFingerprint",
                table: "Executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputFingerprint",
                table: "Executions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // EF's SQLite generator rewrites a column drop as a full table rebuild, which needs a model
        // snapshot this project does not carry. SQLite has supported DROP COLUMN natively since
        // 3.35, so issue it directly.
        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.Sql("ALTER TABLE \"Executions\" DROP COLUMN \"InputFingerprint\";");
            return;
        }

        migrationBuilder.DropColumn(name: "InputFingerprint", table: "Executions");
    }
}
