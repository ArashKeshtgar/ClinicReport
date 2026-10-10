using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicReport.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RolesPermissionsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockoutEndUtc",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Services",
                table: "Users",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TwoFactorEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "TwoFactorLastStep",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorSecret",
                table: "Users",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Permissions = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    RequireTwoFactor = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReportId = table.Column<int>(type: "INTEGER", nullable: true),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_ActorUserId_At",
                table: "SecurityEvents",
                columns: new[] { "ActorUserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_At",
                table: "SecurityEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_ReportId_At",
                table: "SecurityEvents",
                columns: new[] { "ReportId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            // Existing installations: turn the old single Role column into the built-in roles.
            // (RoleSeeder creates the same roles on a new database; it leaves these alone.)
            migrationBuilder.Sql("""
                INSERT INTO Roles (Name, Description, Permissions, RequireTwoFactor, IsSystem) VALUES
                ('Administrator', 'Full access, including users, roles and the audit log.',
                 'reports.view reports.create reports.edit reports.sign patients.manage templates.design users.manage audit.view', 0, 1),
                ('Physician', 'Reads, edits and signs reports.',
                 'reports.view reports.create reports.edit reports.sign patients.manage', 0, 1),
                ('Typist', 'Starts and fills reports during the exam.',
                 'reports.view reports.create reports.edit patients.manage', 0, 1);
                INSERT INTO UserRoles (UserId, RoleId)
                SELECT u.Id, r.Id FROM Users u JOIN Roles r ON r.Name =
                    CASE u.Role WHEN 'Admin' THEN 'Administrator' WHEN 'Physician' THEN 'Physician' ELSE 'Typist' END;
                UPDATE Users SET SecurityStamp = lower(hex(randomblob(16)));
                """);

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockoutEndUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Services",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorLastStep",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorSecret",
                table: "Users");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }
    }
}
