using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceId",
                table: "Batches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_DeviceId",
                table: "Batches",
                column: "DeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Batches_DeviceId",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "Batches");
        }
    }
}
