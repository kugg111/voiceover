using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Server.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceServerKeyWithPerMessageEnvelopeEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerMemberKeys");

            migrationBuilder.CreateTable(
                name: "MessageRecipientKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    WrappedKey = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRecipientKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageRecipientKeys_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipientKeys_MessageId_UserId",
                table: "MessageRecipientKeys",
                columns: new[] { "MessageId", "UserId" },
                unique: true);

            // Data-only cleanup, not a schema change: every existing channel
            // message's Content is ciphertext under the now-deleted
            // ServerMemberKeys scheme (one shared key per server) and has no
            // MessageRecipientKeys envelope under the new per-message scheme
            // this migration introduces - it can never be decrypted by
            // anyone going forward regardless. Wiping it outright (rather
            // than leaving permanently-undecryptable rows behind) was
            // explicitly approved in place of a migration path, since this
            // is dev/early-stage data. DirectMessages are untouched - DM
            // encryption was never part of this scheme and is unaffected.
            migrationBuilder.Sql("DELETE FROM \"MessageReactions\";");
            migrationBuilder.Sql("DELETE FROM \"Messages\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageRecipientKeys");

            migrationBuilder.CreateTable(
                name: "ServerMemberKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GuildServerId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    WrappedByUserId = table.Column<int>(type: "integer", nullable: false),
                    WrappedKey = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerMemberKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerMemberKeys_GuildServerId_UserId",
                table: "ServerMemberKeys",
                columns: new[] { "GuildServerId", "UserId" },
                unique: true);
        }
    }
}
