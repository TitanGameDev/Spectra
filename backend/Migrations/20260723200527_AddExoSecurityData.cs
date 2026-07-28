using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExoSecurityData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExoAcceptedDomainsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoAntiPhishPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoDkimSigningConfigsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoHostedContentFilterPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoHostedOutboundSpamFilterPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExoLastCollectedAt",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoLastError",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoMalwareFilterPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoOrganizationConfigJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExoRoleAssigned",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExoSafeAttachmentPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoSafeLinksPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoSharingPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoTransportRulesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExoAcceptedDomainsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoAntiPhishPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoDkimSigningConfigsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoHostedContentFilterPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoHostedOutboundSpamFilterPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoLastCollectedAt",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoLastError",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoMalwareFilterPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoOrganizationConfigJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoRoleAssigned",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoSafeAttachmentPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoSafeLinksPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoSharingPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoTransportRulesJson",
                table: "Customers");
        }
    }
}
