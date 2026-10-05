using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Msm.Portfolio.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeSubscriptionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "MaintenanceSubscriptions",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // Matches the entity's own default. No row predates this column today —
                // maintenance has always shipped switched off — but this is what any row
                // created before Stripe existed would have meant anyway.
                defaultValue: "Stripe");

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "ClientProfiles",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_StripeCustomerId",
                table: "ClientProfiles",
                column: "StripeCustomerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_StripeCustomerId",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "MaintenanceSubscriptions");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "ClientProfiles");
        }
    }
}
