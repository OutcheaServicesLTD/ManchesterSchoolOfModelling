using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Msm.Portfolio.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaQualityMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Clipping",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Contrast",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Exposure",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sharpness",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Clipping",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Contrast",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Exposure",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Sharpness",
                table: "MediaAssets");
        }
    }
}
