using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gesaicon.Api.Migrations
{
    public partial class AddAnalysisFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalysisMarkdown",
                table: "ExpenseTickets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnalysisJson",
                table: "ExpenseTickets",
                type: "nvarchar(max)",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisMarkdown",
                table: "ExpenseTickets");

            migrationBuilder.DropColumn(
                name: "AnalysisJson",
                table: "ExpenseTickets");
        }
    }
}
