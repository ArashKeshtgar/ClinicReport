using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicReport.Api.Data.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class SignupEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailTokenExpiresAt",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailTokenHash",
                table: "Tenants",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAt",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "Tenants",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailTokenExpiresAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "EmailTokenHash",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "Tenants");
        }
    }
}
