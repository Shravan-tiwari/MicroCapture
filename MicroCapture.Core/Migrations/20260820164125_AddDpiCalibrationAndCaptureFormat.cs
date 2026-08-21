using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDpiCalibrationAndCaptureFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: EF's migration diff initially proposed renaming the pre-existing
            // "UseAltBoundaryPipeline" column to "BleedthroughEnabled" here, because
            // BleedthroughEnabled was added to the C# model (Batch.cs) in a prior commit but was
            // never captured by an EF migration — it only ever existed on real installed
            // databases via CaptureQueueService.EnsureColumn, so this is the first migration EF
            // has generated since. That inferred rename is WRONG: UseAltBoundaryPipeline is a
            // separate, deliberately-orphaned column (see Batch.cs's own comment on it) that
            // must be left alone, and BleedthroughEnabled already exists as its own real column
            // on every installed database via EnsureColumn. Replaced the rename with a proper
            // AddColumn for BleedthroughEnabled instead, matching its actual on-disk shape.
            migrationBuilder.AddColumn<bool>(
                name: "BleedthroughEnabled",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CaptureFormat",
                table: "CaptureJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "TIFF");

            migrationBuilder.AddColumn<string>(
                name: "ProcessedFilePath",
                table: "CaptureJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MeasuredPixelHeight",
                table: "CameraCalibrations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MeasuredPixelWidth",
                table: "CameraCalibrations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TargetHeightInches",
                table: "CameraCalibrations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TargetWidthInches",
                table: "CameraCalibrations",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptureFormat",
                table: "CaptureJobs");

            migrationBuilder.DropColumn(
                name: "ProcessedFilePath",
                table: "CaptureJobs");

            migrationBuilder.DropColumn(
                name: "MeasuredPixelHeight",
                table: "CameraCalibrations");

            migrationBuilder.DropColumn(
                name: "MeasuredPixelWidth",
                table: "CameraCalibrations");

            migrationBuilder.DropColumn(
                name: "TargetHeightInches",
                table: "CameraCalibrations");

            migrationBuilder.DropColumn(
                name: "TargetWidthInches",
                table: "CameraCalibrations");

            migrationBuilder.DropColumn(
                name: "BleedthroughEnabled",
                table: "Batches");
        }
    }
}
