using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMentionEveryonePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only migration - no column change (ServerPermission.
            // MentionEveryone is just a new bit value in the existing int
            // column, see Models/Membership.cs). Upgrades every Membership
            // row that had the old "full" value (15 = ManageChannels|
            // KickMembers|ManageMessages|MuteMembers) to the new one (31,
            // adding MentionEveryone=16) - same "don't silently strip an
            // existing Moderator's granted powers when a new permission bit
            // is introduced" precedent as AddModerationToolsAndPermissions.
            // Rows with a deliberately-restricted custom subset (anything
            // other than 15) are left untouched.
            migrationBuilder.Sql("UPDATE \"Memberships\" SET \"Permissions\" = 31 WHERE \"Permissions\" = 15;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Memberships\" SET \"Permissions\" = 15 WHERE \"Permissions\" = 31;");
        }
    }
}
