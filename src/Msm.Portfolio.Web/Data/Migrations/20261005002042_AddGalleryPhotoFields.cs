using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Msm.Portfolio.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGalleryPhotoFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GalleryReviewNote",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GalleryReviewedAt",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GalleryReviewedByUserId",
                table: "MediaAssets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GalleryStatus",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_ClientId_MediaType",
                table: "MediaAssets",
                columns: new[] { "ClientId", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_GalleryReviewedByUserId",
                table: "MediaAssets",
                column: "GalleryReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_MediaType_GalleryStatus",
                table: "MediaAssets",
                columns: new[] { "MediaType", "GalleryStatus" });

            migrationBuilder.AddForeignKey(
                name: "FK_MediaAssets_AspNetUsers_GalleryReviewedByUserId",
                table: "MediaAssets",
                column: "GalleryReviewedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaAssets_AspNetUsers_GalleryReviewedByUserId",
                table: "MediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_ClientId_MediaType",
                table: "MediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_GalleryReviewedByUserId",
                table: "MediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_MediaType_GalleryStatus",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "GalleryReviewNote",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "GalleryReviewedAt",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "GalleryReviewedByUserId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "GalleryStatus",
                table: "MediaAssets");
        }
    }
}
