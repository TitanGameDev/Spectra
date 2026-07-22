using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InboxRulesJson",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InboxRulesJson",
                table: "CustomerUsers");
        }
    }
}
