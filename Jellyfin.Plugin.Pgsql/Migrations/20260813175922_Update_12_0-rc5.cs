using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    [DbContext(typeof(JellyfinDbContext))]
    [Migration("20260813175922_Update_12_0-rc5")]
    public partial class Update_12_0rc5 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PeopleBaseItemMap_PeopleId",
                table: "PeopleBaseItemMap");

            migrationBuilder.CreateIndex(
                name: "IX_PeopleBaseItemMap_PeopleId_ItemId",
                table: "PeopleBaseItemMap",
                columns: new[] { "PeopleId", "ItemId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PeopleBaseItemMap_PeopleId_ItemId",
                table: "PeopleBaseItemMap");

            migrationBuilder.CreateIndex(
                name: "IX_PeopleBaseItemMap_PeopleId",
                table: "PeopleBaseItemMap",
                column: "PeopleId");
        }
    }
}
