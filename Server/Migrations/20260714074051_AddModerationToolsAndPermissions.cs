using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Server.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationToolsAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfills every existing Membership row (Member and Moderator
            // alike - the column is only ever consulted for Moderator rows,
            // see PermissionService.HasPermissionAsync) with All (15 =
            // ManageChannels|KickMembers|ManageMessages|MuteMembers), not 0
            // - the C# model's own `= ServerPermission.All` property
            // initializer only applies to newly-constructed objects in
            // application code, not to this migration's column-level
            // default for rows that already exist, so it has to be spelled
            // out explicitly here. Getting this wrong would silently strip
            // every Moderator promoted before this feature existed down to
            // zero permissions the moment this migration runs.
            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "Memberships",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "SlowModeSeconds",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "BannedUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildServerId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    BannedByUserId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannedUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModerationLogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildServerId = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<int>(type: "integer", nullable: false),
                    ActorUsername = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    TargetUserId = table.Column<int>(type: "integer", nullable: true),
                    TargetUsername = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BannedUsers_GuildServerId_UserId",
                table: "BannedUsers",
                columns: new[] { "GuildServerId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModerationLogEntries_GuildServerId_CreatedAt",
                table: "ModerationLogEntries",
                columns: new[] { "GuildServerId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BannedUsers");

            migrationBuilder.DropTable(
                name: "ModerationLogEntries");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "SlowModeSeconds",
                table: "Channels");
        }
    }
}
