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
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "Users"
                        GROUP BY UPPER("Username")
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot apply Jellyfin 10.11.10 PostgreSQL migration: duplicate usernames collide after normalization. Run SELECT UPPER("Username"), COUNT(*) FROM "Users" GROUP BY UPPER("Username") HAVING COUNT(*) > 1; and resolve the duplicate users before retrying. No users were changed automatically.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUsername",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Users"
                SET "NormalizedUsername" = UPPER("Username")
                WHERE "NormalizedUsername" IS NULL;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "Users"
                        WHERE "NormalizedUsername" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot apply Jellyfin 10.11.10 PostgreSQL migration: NormalizedUsername backfill left NULL values. Restore from the pre-upgrade pg_dump and inspect the Users table before retrying.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "Users"
                        GROUP BY "NormalizedUsername"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot apply Jellyfin 10.11.10 PostgreSQL migration: duplicate usernames collide after NormalizedUsername backfill. Restore from the pre-upgrade pg_dump, resolve case-insensitive duplicates manually, and retry. No users were fixed automatically.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedUsername",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

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
