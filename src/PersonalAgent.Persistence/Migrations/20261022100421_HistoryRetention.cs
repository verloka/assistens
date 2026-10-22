using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoryRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContentClearedAtUtc",
                table: "TaskRuns",
                type: "INTEGER",
                nullable: true);

            // The owner's privacy exception permits only irreversible content redaction.
            // Event identity, ordering and safety metadata remain append-only.
            migrationBuilder.Sql("DROP TRIGGER RunEvents_NoUpdate;");
            migrationBuilder.Sql("""
                CREATE TRIGGER RunEvents_NoUpdate BEFORE UPDATE ON RunEvents
                WHEN NOT (
                    NEW.Id IS OLD.Id AND NEW.RunId IS OLD.RunId AND NEW.Sequence IS OLD.Sequence
                    AND NEW.EventType IS OLD.EventType AND NEW.TimestampUtc IS OLD.TimestampUtc
                    AND NEW.ToolId IS OLD.ToolId AND NEW.ToolCallId IS OLD.ToolCallId
                    AND NEW.Message = '[Private history cleared]' AND NEW.PayloadJson IS NULL
                    AND EXISTS (SELECT 1 FROM TaskRuns WHERE Id = OLD.RunId AND ContentClearedAtUtc IS NOT NULL
                        AND Status IN ('Completed','Failed','Cancelled','Skipped')))
                BEGIN SELECT RAISE(ABORT, 'RunEvents are append-only except privacy redaction'); END;
                """);

            migrationBuilder.CreateTable(
                name: "HistoryRetention",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryRetention", x => x.Id);
                    table.CheckConstraint("CK_HistoryRetention_Policy", "Id = 1 AND Days BETWEEN 1 AND 36500");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER RunEvents_NoUpdate;");
            migrationBuilder.Sql("""
                CREATE TRIGGER RunEvents_NoUpdate BEFORE UPDATE ON RunEvents
                BEGIN SELECT RAISE(ABORT, 'RunEvents are append-only'); END;
                """);
            migrationBuilder.DropTable(
                name: "HistoryRetention");

            migrationBuilder.DropColumn(
                name: "ContentClearedAtUtc",
                table: "TaskRuns");
        }
    }
}
