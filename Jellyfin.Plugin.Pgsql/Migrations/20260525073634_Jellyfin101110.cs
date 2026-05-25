using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class Jellyfin101110 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "Users_Backup_20260525073634_Jellyfin101110" AS TABLE "Users";
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory_Backup_20260525073634_Jellyfin101110" AS TABLE "__EFMigrationsHistory";
                """);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUsername",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE "Users"
                SET "NormalizedUsername" = UPPER("Username");
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "Users"
                        GROUP BY "NormalizedUsername"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot apply Jellyfin 10.11.10 PostgreSQL migration: duplicate usernames collide after normalization. Review "Users_Backup_20260525073634_Jellyfin101110" and resolve case-insensitive duplicates before retrying.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedUsername",
                table: "Users");
        }
    }
}
