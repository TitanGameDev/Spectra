using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxAndLicenseData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedDateTime",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Department",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasArchiveMailbox",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicensesJson",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MailboxItemCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MailboxSizeBytes",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfficeLocation",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedDateTime",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "Department",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "HasArchiveMailbox",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "LicensesJson",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "MailboxItemCount",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "MailboxSizeBytes",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "OfficeLocation",
                table: "CustomerUsers");
        }
    }
}
