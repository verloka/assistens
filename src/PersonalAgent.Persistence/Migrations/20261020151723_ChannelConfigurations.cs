using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerSource = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelConfigurations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConfigurations_ChannelId_RemoteIdentity",
                table: "ChannelConfigurations",
                columns: new[] { "ChannelId", "RemoteIdentity" },
                unique: true,
                filter: "\"Enabled\" = 1 AND \"RemoteIdentity\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConfigurations_OwnerSource_OwnerId_ChannelId_ConnectionId_DestinationId",
                table: "ChannelConfigurations",
                columns: new[] { "OwnerSource", "OwnerId", "ChannelId", "ConnectionId", "DestinationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelConfigurations");
        }
    }
}
