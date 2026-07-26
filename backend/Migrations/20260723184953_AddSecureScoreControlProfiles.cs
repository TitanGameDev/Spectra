using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureScoreControlProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecureScoreControlProfilesJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecureScoreControlProfilesJson",
                table: "Customers");
        }
    }
}
