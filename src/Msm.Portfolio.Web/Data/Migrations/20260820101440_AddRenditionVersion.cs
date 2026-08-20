using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Msm.Portfolio.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRenditionVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RenditionVersion",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenditionVersion",
                table: "MediaAssets");
        }
    }
}
