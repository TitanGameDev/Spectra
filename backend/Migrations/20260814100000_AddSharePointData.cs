using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSharePointData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharePointSitesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OneDriveActiveFileCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OneDriveFileCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OneDriveLastActivityDate",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OneDriveSiteUrl",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OneDriveStorageAllocatedBytes",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OneDriveStorageUsedBytes",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharePointSitesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "OneDriveActiveFileCount",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "OneDriveFileCount",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "OneDriveLastActivityDate",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "OneDriveSiteUrl",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "OneDriveStorageAllocatedBytes",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "OneDriveStorageUsedBytes",
                table: "CustomerUsers");
        }
    }
}
