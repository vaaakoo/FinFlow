using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Audit.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEvents", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_EntityId_OccurredAt",
                table: "WorkflowEvents",
                columns: new[] { "EntityId", "OccurredAt" });

            migrationBuilder.Sql("""
                CREATE TRIGGER immutable_workflow_events BEFORE UPDATE OR DELETE ON "WorkflowEvents"
                    FOR EACH ROW EXECUTE FUNCTION finflow_reject_audit_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowEvents");
        }
    }
}
