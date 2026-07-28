using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAzureResourceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AzureAppServicesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureLastCollectedAt",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureLastError",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureReservationsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureSubscriptionsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureVirtualMachinesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntraAppRegistrationsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntraServicePrincipalsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AzureAppServicesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AzureLastCollectedAt",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AzureLastError",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AzureReservationsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AzureSubscriptionsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AzureVirtualMachinesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "EntraAppRegistrationsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "EntraServicePrincipalsJson",
                table: "Customers");
        }
    }
}
