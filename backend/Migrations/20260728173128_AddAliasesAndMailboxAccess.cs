using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAliasesAndMailboxAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AliasesJson",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoMailboxPermissionsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExoRecipientPermissionsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AliasesJson",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "ExoMailboxPermissionsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExoRecipientPermissionsJson",
                table: "Customers");
        }
    }
}
