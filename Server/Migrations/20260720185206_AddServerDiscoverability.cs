using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Server.Migrations
{
    /// <inheritdoc />
    public partial class AddServerDiscoverability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "GuildServers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "GuildServers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_GuildServers_IsPublic",
                table: "GuildServers",
                column: "IsPublic");

            migrationBuilder.CreateIndex(
                name: "IX_GuildServers_Name_Trgm",
                table: "GuildServers",
                column: "Name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GuildServers_IsPublic",
                table: "GuildServers");

            migrationBuilder.DropIndex(
                name: "IX_GuildServers_Name_Trgm",
                table: "GuildServers");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "GuildServers");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "GuildServers");
        }
    }
}
