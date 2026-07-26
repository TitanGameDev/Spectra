using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSccComplianceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SccAlertPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SccDlpPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccLastCollectedAt",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SccLastError",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SccRetentionPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SccAlertPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SccDlpPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SccLastCollectedAt",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SccLastError",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SccRetentionPoliciesJson",
                table: "Customers");
        }
    }
}
