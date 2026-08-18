using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace iRoute.Infrastructure.Migrations;

[DbContext(typeof(IRouteDbContext))]
[Migration(MigrationId)]
public sealed class RoutingDecisionCheckpoint : Migration
{
    public const string MigrationId = "20260801010000_RoutingDecisionCheckpoint";

    private const string LegacyRoutingJson =
        "{\"policyVersion\":\"legacy\",\"path\":\"Direct\",\"reason\":\"Migrated workflow checkpoint.\"," +
        "\"selectedCapability\":\"legacy\",\"selectedProfileId\":null,\"selectedModelTier\":null," +
        "\"qualityFloor\":0,\"expectedQuality\":0,\"expectedCost\":0,\"expectedLatencyMilliseconds\":0," +
        "\"uncertainty\":0,\"score\":0,\"plannerInvoked\":false,\"planningCalls\":0," +
        "\"escalated\":false,\"escalationReason\":null,\"candidates\":[]}";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) is true)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoutingJson",
                table: "WorkflowPlans",
                type: "text",
                nullable: false,
                defaultValue: LegacyRoutingJson);
            return;
        }

        if (ActiveProvider?.Contains("Sqlite", StringComparison.Ordinal) is true)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoutingJson",
                table: "WorkflowPlans",
                type: "TEXT",
                nullable: false,
                defaultValue: LegacyRoutingJson);
            return;
        }

        throw new NotSupportedException($"Migration provider '{ActiveProvider}' is not supported.");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RoutingJson", table: "WorkflowPlans");
    }
}
