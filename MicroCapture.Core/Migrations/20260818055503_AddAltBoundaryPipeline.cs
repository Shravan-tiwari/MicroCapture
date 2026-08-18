using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MicroCapture.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAltBoundaryPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseAltBoundaryPipeline",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseAltBoundaryPipeline",
                table: "Batches");
        }
    }
}
