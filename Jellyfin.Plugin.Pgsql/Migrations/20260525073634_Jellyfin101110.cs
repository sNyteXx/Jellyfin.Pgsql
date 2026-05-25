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

                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "NormalizedUsername" character varying(255);

                UPDATE "Users"
                SET "NormalizedUsername" = UPPER("Username")
                WHERE "NormalizedUsername" IS NULL;

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

                ALTER TABLE "Users" ALTER COLUMN "NormalizedUsername" SET NOT NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_NormalizedUsername" ON "Users" ("NormalizedUsername");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Users_NormalizedUsername";
                ALTER TABLE "Users" DROP COLUMN IF EXISTS "NormalizedUsername";
                """);
        }
    }
}
