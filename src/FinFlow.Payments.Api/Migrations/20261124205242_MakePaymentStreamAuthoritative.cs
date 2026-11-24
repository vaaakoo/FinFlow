using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Payments.Api.Migrations
{
    /// <inheritdoc />
    public partial class MakePaymentStreamAuthoritative : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AmountMinor",
                table: "Events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedAttempts",
                table: "Events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttempt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestKey",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scenario",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "Events",
                type: "text",
                nullable: true);

            // Existing streams recorded the request and retry schedule only on the projection.
            migrationBuilder.Sql("""
                UPDATE "Events" e
                SET "AccountId" = p."AccountId", "AmountMinor" = p."AmountMinor",
                    "RequestKey" = p."Key", "Scenario" = p."Scenario", "Subject" = p."Subject"
                FROM "Payments" p
                WHERE e."PaymentId" = p."Id" AND e."Version" = 1;

                UPDATE "Events" e
                SET "NextAttempt" = p."NextAttempt", "FailedAttempts" = p."FailedAttempts"
                FROM "Payments" p
                WHERE e."PaymentId" = p."Id" AND e."Version" = p."Version";

                CREATE FUNCTION finflow_reject_payment_event_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Payment events are append-only' USING ERRCODE = 'check_violation';
                END;
                $$;

                CREATE TRIGGER immutable_payment_events BEFORE UPDATE OR DELETE ON "Events"
                    FOR EACH ROW EXECUTE FUNCTION finflow_reject_payment_event_change();
                CREATE TRIGGER no_truncate_payment_events BEFORE TRUNCATE ON "Events"
                    FOR EACH STATEMENT EXECUTE FUNCTION finflow_reject_payment_event_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER no_truncate_payment_events ON "Events";
                DROP TRIGGER immutable_payment_events ON "Events";
                DROP FUNCTION finflow_reject_payment_event_change();
                """);

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "AmountMinor",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "FailedAttempts",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "NextAttempt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RequestKey",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Scenario",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "Events");
        }
    }
}
