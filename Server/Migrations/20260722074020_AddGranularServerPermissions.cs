using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGranularServerPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only migration - no column change, same shape as
            // AddMentionEveryonePermission (three more bits added to the
            // existing int column: ManageRoles=32, ManageServer=64,
            // ViewAuditLog=128, see Models/Membership.cs). Upgrades every
            // Membership row that had the previous "full" value (31 = the
            // four original flags + MentionEveryone) to the new one (255,
            // adding all three) - same "don't silently strip an existing
            // Moderator's granted powers when new permission bits are
            // introduced" precedent. Rows with a deliberately-restricted
            // custom subset (anything other than 31) are left untouched.
            migrationBuilder.Sql("UPDATE \"Memberships\" SET \"Permissions\" = 255 WHERE \"Permissions\" = 31;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Memberships\" SET \"Permissions\" = 31 WHERE \"Permissions\" = 255;");
        }
    }
}
