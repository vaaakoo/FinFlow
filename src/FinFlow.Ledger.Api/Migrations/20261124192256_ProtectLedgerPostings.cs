using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Ledger.Api.Migrations
{
    /// <inheritdoc />
    public partial class ProtectLedgerPostings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Transfers" ADD CONSTRAINT "CK_Transfer_Posting"
                    CHECK ("AmountMinor" > 0 AND "SourceAccountId" <> "DestinationAccountId");

                CREATE FUNCTION finflow_reject_posting_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Posted financial records are immutable' USING ERRCODE = 'check_violation';
                END;
                $$;

                CREATE TRIGGER immutable_entries BEFORE UPDATE OR DELETE ON "Entries"
                    FOR EACH ROW EXECUTE FUNCTION finflow_reject_posting_change();
                CREATE TRIGGER immutable_transfers BEFORE UPDATE OR DELETE ON "Transfers"
                    FOR EACH ROW EXECUTE FUNCTION finflow_reject_posting_change();

                CREATE FUNCTION finflow_check_posting() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    transfer_id uuid;
                    posting "Transfers"%ROWTYPE;
                BEGIN
                    IF TG_TABLE_NAME = 'Transfers' THEN
                        transfer_id := NEW."Id";
                    ELSE
                        transfer_id := NEW."TransferId";
                    END IF;

                    SELECT * INTO STRICT posting FROM "Transfers" WHERE "Id" = transfer_id;
                    IF (SELECT count(*) FROM "Entries" WHERE "TransferId" = transfer_id) <> 2
                        OR NOT EXISTS (SELECT FROM "Entries" WHERE "TransferId" = transfer_id
                            AND "AccountId" = posting."SourceAccountId" AND "DeltaMinor" = -posting."AmountMinor")
                        OR NOT EXISTS (SELECT FROM "Entries" WHERE "TransferId" = transfer_id
                            AND "AccountId" = posting."DestinationAccountId" AND "DeltaMinor" = posting."AmountMinor")
                    THEN
                        RAISE EXCEPTION 'Transfer must post one matching debit and credit'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER balanced_transfers AFTER INSERT ON "Transfers"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finflow_check_posting();
                CREATE CONSTRAINT TRIGGER balanced_entries AFTER INSERT ON "Entries"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finflow_check_posting();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER balanced_entries ON "Entries";
                DROP TRIGGER balanced_transfers ON "Transfers";
                DROP FUNCTION finflow_check_posting();
                DROP TRIGGER immutable_entries ON "Entries";
                DROP TRIGGER immutable_transfers ON "Transfers";
                DROP FUNCTION finflow_reject_posting_change();
                ALTER TABLE "Transfers" DROP CONSTRAINT "CK_Transfer_Posting";
                """);

        }
    }
}
