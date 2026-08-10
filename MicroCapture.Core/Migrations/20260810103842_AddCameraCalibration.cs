using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraCalibration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DewarpCurve",
                table: "CaptureJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DewarpManualOverrideApplied",
                table: "CaptureJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BinarizeEnabled",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CameraCalibrationId",
                table: "Batches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DewarpEnabled",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Dpi",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CameraCalibrations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    CalibratedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    CameraMatrix = table.Column<string>(type: "TEXT", nullable: false),
                    DistCoeffs = table.Column<string>(type: "TEXT", nullable: false),
                    ReprojectionErrorPx = table.Column<double>(type: "REAL", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraCalibrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_CameraCalibrationId",
                table: "Batches",
                column: "CameraCalibrationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Batches_CameraCalibrations_CameraCalibrationId",
                table: "Batches",
                column: "CameraCalibrationId",
                principalTable: "CameraCalibrations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Batches_CameraCalibrations_CameraCalibrationId",
                table: "Batches");

            migrationBuilder.DropTable(
                name: "CameraCalibrations");

            migrationBuilder.DropIndex(
                name: "IX_Batches_CameraCalibrationId",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "DewarpCurve",
                table: "CaptureJobs");

            migrationBuilder.DropColumn(
                name: "DewarpManualOverrideApplied",
                table: "CaptureJobs");

            migrationBuilder.DropColumn(
                name: "BinarizeEnabled",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "CameraCalibrationId",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "DewarpEnabled",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "Dpi",
                table: "Batches");
        }
    }
}
