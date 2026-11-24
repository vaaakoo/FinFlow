using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Ledger.Api.Migrations
{
    /// <inheritdoc />
    public partial class ScopePostingKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transfers_IdempotencyKey",
                table: "Transfers");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "Transfers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "http");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_Scope_IdempotencyKey",
                table: "Transfers",
                columns: new[] { "Scope", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transfers_Scope_IdempotencyKey",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "Transfers");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_IdempotencyKey",
                table: "Transfers",
                column: "IdempotencyKey",
                unique: true);
        }
    }
}
