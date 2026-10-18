using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunSubmissionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmissionHash",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmissionKey",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmissionSource",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_SubmissionSource_SubmissionKey",
                table: "TaskRuns",
                columns: new[] { "SubmissionSource", "SubmissionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskRuns_SubmissionSource_SubmissionKey",
                table: "TaskRuns");

            migrationBuilder.DropColumn(
                name: "SubmissionHash",
                table: "TaskRuns");

            migrationBuilder.DropColumn(
                name: "SubmissionKey",
                table: "TaskRuns");

            migrationBuilder.DropColumn(
                name: "SubmissionSource",
                table: "TaskRuns");
        }
    }
}
