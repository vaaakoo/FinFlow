using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Ledger.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerSubject",
                table: "Accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_OwnerSubject_Id",
                table: "Accounts",
                columns: new[] { "OwnerSubject", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_OwnerSubject_Id",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OwnerSubject",
                table: "Accounts");
        }
    }
}
