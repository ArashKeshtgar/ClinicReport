using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicReport.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FinanceFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OhipProfessionalFee",
                table: "Templates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OhipTechnicalFee",
                table: "Templates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FinanceExports",
                columns: table => new
                {
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceExports", x => x.ExternalId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinanceExports");

            migrationBuilder.DropColumn(
                name: "OhipProfessionalFee",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "OhipTechnicalFee",
                table: "Templates");
        }
    }
}
