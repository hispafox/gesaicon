using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gesaicon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyAndPathFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanySlug",
                table: "ExpenseTickets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpenseMonth",
                table: "ExpenseTickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpenseYear",
                table: "ExpenseTickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelativePath",
                table: "ExpenseTickets",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompanySlug",
                table: "ExpenseTickets");

            migrationBuilder.DropColumn(
                name: "ExpenseMonth",
                table: "ExpenseTickets");

            migrationBuilder.DropColumn(
                name: "ExpenseYear",
                table: "ExpenseTickets");

            migrationBuilder.DropColumn(
                name: "RelativePath",
                table: "ExpenseTickets");
        }
    }
}
