using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gesaicon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLastErrorMessageToExpenseTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastErrorMessage",
                table: "ExpenseTickets",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastErrorMessage",
                table: "ExpenseTickets");
        }
    }
}
