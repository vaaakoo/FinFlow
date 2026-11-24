using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinFlow.Transfers.Api.Migrations
{
    /// <inheritdoc />
    public partial class TraceTransferDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "Orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "Orders");
        }
    }
}
