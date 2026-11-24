using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Audit.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransferEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CausationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferEvents", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferEvents_CompletedAt_EventId",
                table: "TransferEvents",
                columns: new[] { "CompletedAt", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferEvents_TransferId",
                table: "TransferEvents",
                column: "TransferId");

            migrationBuilder.Sql("""
                CREATE FUNCTION finflow_reject_audit_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Audit events are append-only' USING ERRCODE = 'check_violation';
                END;
                $$;
                CREATE TRIGGER immutable_audit_events BEFORE UPDATE OR DELETE ON "TransferEvents"
                    FOR EACH ROW EXECUTE FUNCTION finflow_reject_audit_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferEvents");
            migrationBuilder.Sql("DROP FUNCTION finflow_reject_audit_change();");
        }
    }
}
