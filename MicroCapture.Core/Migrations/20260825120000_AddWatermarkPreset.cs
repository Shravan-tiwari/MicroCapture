using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWatermarkPreset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WatermarkEnabled",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WatermarkPresetId",
                table: "Batches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WatermarkPresets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WatermarkType = table.Column<string>(type: "TEXT", nullable: false),
                    TextContent = table.Column<string>(type: "TEXT", nullable: true),
                    FontFamily = table.Column<string>(type: "TEXT", nullable: true),
                    FontSize = table.Column<double>(type: "REAL", nullable: false),
                    TextColor = table.Column<string>(type: "TEXT", nullable: true),
                    LogoImagePath = table.Column<string>(type: "TEXT", nullable: true),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<double>(type: "REAL", nullable: false),
                    Height = table.Column<double>(type: "REAL", nullable: false),
                    RotationDegrees = table.Column<double>(type: "REAL", nullable: false),
                    Opacity = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatermarkPresets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_WatermarkPresetId",
                table: "Batches",
                column: "WatermarkPresetId");

            migrationBuilder.AddForeignKey(
                name: "FK_Batches_WatermarkPresets_WatermarkPresetId",
                table: "Batches",
                column: "WatermarkPresetId",
                principalTable: "WatermarkPresets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Batches_WatermarkPresets_WatermarkPresetId",
                table: "Batches");

            migrationBuilder.DropTable(
                name: "WatermarkPresets");

            migrationBuilder.DropIndex(
                name: "IX_Batches_WatermarkPresetId",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "WatermarkEnabled",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "WatermarkPresetId",
                table: "Batches");
        }
    }
}
