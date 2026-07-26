using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExoThirdBatchChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExoAtpPolicyForO365Json",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoMailboxAuditBypassJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoRemoteDomainsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExoAtpPolicyForO365Json",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoMailboxAuditBypassJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoRemoteDomainsJson",
                table: "Customers");
        }
    }
}
