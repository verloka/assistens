using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentSessionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_RunId",
                table: "AgentSessions",
                column: "RunId",
                unique: true,
                filter: "\"RunId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_TaskRuns_RunId",
                table: "AgentSessions",
                column: "RunId",
                principalTable: "TaskRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_TaskRuns_RunId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_RunId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "AgentSessions");
        }
    }
}
