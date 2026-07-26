using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxForwardingAndConcealedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExoMailboxForwardingJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MailboxDataConcealed",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExoMailboxForwardingJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "MailboxDataConcealed",
                table: "Customers");
        }
    }
}
