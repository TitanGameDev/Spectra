using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spectra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamsData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TeamsJson",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamsCallCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamsChatMessageCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TeamsLastActivityDate",
                table: "CustomerUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamsMeetingCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeamsPrivateChatMessageCount",
                table: "CustomerUsers",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeamsJson",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TeamsCallCount",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "TeamsChatMessageCount",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "TeamsLastActivityDate",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "TeamsMeetingCount",
                table: "CustomerUsers");

            migrationBuilder.DropColumn(
                name: "TeamsPrivateChatMessageCount",
                table: "CustomerUsers");
        }
    }
}
