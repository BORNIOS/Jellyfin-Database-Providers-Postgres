using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Postgres.Data.Migrations
{
    /// <summary>
    /// Jellyfin 12.4: the <c>OwnerId</c> and <c>(ExtraType, OwnerId)</c> indexes of <c>BaseItems</c> become
    /// partial, matching <c>MakeOwnerIdIndexesPartial</c> of the SQLite provider. Most items have neither an
    /// owner nor an extra type, so the indexes shrink to the rows that are actually looked up.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "EF migration operations execute once and follow the scaffolded array format.")]
    public partial class Jellyfin124 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: new[] { "ExtraType", "OwnerId" },
                filter: "\"ExtraType\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId",
                filter: "\"OwnerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: new[] { "ExtraType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId");
        }
    }
}
