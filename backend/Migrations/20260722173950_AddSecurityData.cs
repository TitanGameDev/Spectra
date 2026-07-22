using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ForwardingRulesJson",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaJson",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionalAccessPoliciesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecureScoreJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForwardingRulesJson",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "MfaJson",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "ConditionalAccessPoliciesJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SecureScoreJson",
                table: "Customers");
        }
    }
}
