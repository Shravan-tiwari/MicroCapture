using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFixedFrameCalibration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FixedFrameImageHeight",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FixedFrameImageWidth",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FixedFrames",
                table: "Batches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredExportFormat",
                table: "Batches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "UseFixedFrames",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FixedFrameImageHeight",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "FixedFrameImageWidth",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "FixedFrames",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "PreferredExportFormat",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "UseFixedFrames",
                table: "Batches");
        }
    }
}
