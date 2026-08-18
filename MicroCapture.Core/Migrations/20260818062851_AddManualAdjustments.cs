using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddManualAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasManualAdjustments",
                table: "CaptureJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RotationDegrees",
                table: "CaptureJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "FlipHorizontal",
                table: "CaptureJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FlipVertical",
                table: "CaptureJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "Brightness",
                table: "CaptureJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Contrast",
                table: "CaptureJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Saturation",
                table: "CaptureJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Sharpness",
                table: "CaptureJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "WhiteBalance",
                table: "CaptureJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "HasManualAdjustments", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "RotationDegrees", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "FlipHorizontal", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "FlipVertical", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "Brightness", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "Contrast", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "Saturation", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "Sharpness", table: "CaptureJobs");
            migrationBuilder.DropColumn(name: "WhiteBalance", table: "CaptureJobs");
        }
    }
}
