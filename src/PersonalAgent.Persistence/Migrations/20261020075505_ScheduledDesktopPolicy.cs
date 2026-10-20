using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledDesktopPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DesktopPolicy",
                table: "Schedules",
                type: "TEXT",
                nullable: false,
                defaultValue: "StartWhenIdle");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Schedules_DesktopPolicy",
                table: "Schedules",
                sql: "DesktopPolicy IN ('StartWhenIdle','StartAfterCountdown','AskEveryTime')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Schedules_DesktopPolicy",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "DesktopPolicy",
                table: "Schedules");
        }
    }
}
