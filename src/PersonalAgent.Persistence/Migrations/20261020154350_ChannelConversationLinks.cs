using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelConversationLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelConversationLinks",
                columns: table => new
                {
                    OwnerSource = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalUpdateId = table.Column<string>(type: "TEXT", nullable: true),
                    OutgoingMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelConversationLinks", x => new { x.OwnerSource, x.OwnerId, x.ChannelId, x.ConnectionId, x.DestinationId, x.SenderId, x.ExternalMessageId });
                    table.ForeignKey(
                        name: "FK_ChannelConversationLinks_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelConversationLinks_TaskRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "TaskRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConversationLinks_ConversationId",
                table: "ChannelConversationLinks",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConversationLinks_OwnerSource_OwnerId_ChannelId_ConnectionId_DestinationId_SenderId_ExternalUpdateId",
                table: "ChannelConversationLinks",
                columns: new[] { "OwnerSource", "OwnerId", "ChannelId", "ConnectionId", "DestinationId", "SenderId", "ExternalUpdateId" },
                unique: true,
                filter: "\"ExternalUpdateId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConversationLinks_RunId",
                table: "ChannelConversationLinks",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelConversationLinks");
        }
    }
}
