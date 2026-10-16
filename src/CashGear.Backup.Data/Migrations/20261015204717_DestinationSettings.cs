using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashGear.Backup.Data.Migrations
{
    /// <inheritdoc />
    public partial class DestinationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ProtectedCredential",
                table: "Destinations",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Settings",
                table: "Destinations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedCredential",
                table: "Destinations");

            migrationBuilder.DropColumn(
                name: "Settings",
                table: "Destinations");
        }
    }
}
