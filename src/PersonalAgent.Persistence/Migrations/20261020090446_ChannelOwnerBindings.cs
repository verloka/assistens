using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelOwnerBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerSource = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelBindings", x => x.Id);
                    table.CheckConstraint("CK_ChannelBindings_Status", "Status IN ('Pending','Approved','Revoked')");
                    table.CheckConstraint("CK_ChannelBindings_Version", "Version >= 0 AND PayloadSchemaVersion > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelBindings_ChannelId_ConnectionId_DestinationId_SenderId",
                table: "ChannelBindings",
                columns: new[] { "ChannelId", "ConnectionId", "DestinationId", "SenderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelBindings_OwnerSource_OwnerId_Status",
                table: "ChannelBindings",
                columns: new[] { "OwnerSource", "OwnerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelBindings");
        }
    }
}
