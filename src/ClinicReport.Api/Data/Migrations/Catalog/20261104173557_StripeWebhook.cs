using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicReport.Api.Data.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class StripeWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastStripeEventAt",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PastDueSince",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProcessedStripeEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedStripeEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_StripeCustomerId",
                table: "Tenants",
                column: "StripeCustomerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedStripeEvents");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_StripeCustomerId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LastStripeEventAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PastDueSince",
                table: "Tenants");
        }
    }
}
