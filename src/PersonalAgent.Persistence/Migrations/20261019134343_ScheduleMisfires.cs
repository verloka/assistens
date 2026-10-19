using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleMisfires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CatchUpRemaining",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CatchUpUntilUtc",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxCatchUpRuns",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "MisfirePolicy",
                table: "Schedules",
                type: "TEXT",
                nullable: false,
                defaultValue: "RunOnce");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Schedules_Misfire",
                table: "Schedules",
                sql: "MisfirePolicy IN ('RunOnce','Skip','CatchUp') AND MaxCatchUpRuns BETWEEN 1 AND 10");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Schedules_Misfire",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "CatchUpRemaining",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "CatchUpUntilUtc",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MaxCatchUpRuns",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MisfirePolicy",
                table: "Schedules");
        }
    }
}
