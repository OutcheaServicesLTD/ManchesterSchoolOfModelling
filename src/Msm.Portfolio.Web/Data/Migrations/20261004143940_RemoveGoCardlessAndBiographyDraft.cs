using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Msm.Portfolio.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGoCardlessAndBiographyDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.RenameColumn(
                name: "GoCardlessReference",
                table: "Orders",
                newName: "StripeCheckoutSessionId");

            migrationBuilder.RenameIndex(
                name: "IX_Orders_GoCardlessReference",
                table: "Orders",
                newName: "IX_Orders_StripeCheckoutSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "StripeCheckoutSessionId",
                table: "Orders",
                newName: "GoCardlessReference");

            migrationBuilder.RenameIndex(
                name: "IX_Orders_StripeCheckoutSessionId",
                table: "Orders",
                newName: "IX_Orders_GoCardlessReference");

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
    }
}
