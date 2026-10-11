using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicReport.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "ReportOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "ReportOrders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "ReportOrders");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "ReportOrders");
        }
    }
}
