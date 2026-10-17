using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Postgres.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeInvalidBaseItemDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "BaseItems"
                SET "DateCreated" = TIMESTAMPTZ '1970-01-01 00:00:00+00'
                WHERE NOT isfinite("DateCreated")
                   OR "DateCreated" = TIMESTAMPTZ '0001-01-01 00:00:00+00';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverting these client-unsafe sentinel values would reintroduce Android TV crashes.
        }
    }
}
