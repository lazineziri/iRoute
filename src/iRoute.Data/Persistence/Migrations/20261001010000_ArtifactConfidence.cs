using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Data.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class ArtifactConfidence : Migration
{
    public const string MigrationId = "20261001010000_ArtifactConfidence";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<decimal>(
        name: "Confidence", table: "Artifacts", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // This additive migration has no generated target model for EF's SQLite table rebuild.
        // The bundled SQLite supports native DROP COLUMN; no artifact content is discarded.
        if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            migrationBuilder.Sql("ALTER TABLE \"Artifacts\" DROP COLUMN \"Confidence\";");
        else migrationBuilder.DropColumn("Confidence", "Artifacts");
    }
}
