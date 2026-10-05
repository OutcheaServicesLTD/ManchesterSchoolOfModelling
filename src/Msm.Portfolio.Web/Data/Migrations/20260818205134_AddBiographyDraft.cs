using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Msm.Portfolio.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBiographyDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BiographyDraft",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BiographyDraftAttempts",
                table: "ClientProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BiographyDraftError",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BiographyDraftGeneratedAt",
                table: "ClientProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BiographyDraftNextAttemptAt",
                table: "ClientProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BiographyDraftStatus",
                table: "ClientProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BiographyDraft",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "BiographyDraftAttempts",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "BiographyDraftError",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "BiographyDraftGeneratedAt",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "BiographyDraftNextAttemptAt",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "BiographyDraftStatus",
                table: "ClientProfiles");
        }
    }
}
