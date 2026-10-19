using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PendingApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: false),
                    RunVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerSource = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    ActionJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.CheckConstraint("CK_ApprovalRequests_Expiry", "ExpiresAtUtc > CreatedAtUtc");
                    table.CheckConstraint("CK_ApprovalRequests_Status", "Status IN ('Pending','Approved','Rejected','Expired')");
                    table.CheckConstraint("CK_ApprovalRequests_Version", "Version >= 0 AND RunVersion >= 0 AND PayloadSchemaVersion > 0");
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_TaskRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "TaskRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_OwnerSource_OwnerId_Status_ExpiresAtUtc",
                table: "ApprovalRequests",
                columns: new[] { "OwnerSource", "OwnerId", "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_RunId_ToolCallId",
                table: "ApprovalRequests",
                columns: new[] { "RunId", "ToolCallId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalRequests");
        }
    }
}
