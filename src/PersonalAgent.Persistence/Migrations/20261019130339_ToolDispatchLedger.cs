using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ToolDispatchLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolCalls", x => x.Id);
                    table.CheckConstraint("CK_ToolCalls_SchemaVersion", "PayloadSchemaVersion > 0");
                    table.CheckConstraint("CK_ToolCalls_Status", "Status IN ('Dispatched','Acknowledged','OutcomeUnknown')");
                    table.ForeignKey(
                        name: "FK_ToolCalls_ApprovalRequests_ApprovalRequestId",
                        column: x => x.ApprovalRequestId,
                        principalTable: "ApprovalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ToolCalls_TaskRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "TaskRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToolCalls_ApprovalRequestId",
                table: "ToolCalls",
                column: "ApprovalRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolCalls_RunId_ToolCallId",
                table: "ToolCalls",
                columns: new[] { "RunId", "ToolCallId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolCalls");
        }
    }
}
