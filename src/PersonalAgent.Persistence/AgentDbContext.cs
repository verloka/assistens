using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PersonalAgent.Core.Domain;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.Persistence;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<ConversationRow> Conversations => Set<ConversationRow>();
    public DbSet<TaskDefinitionRow> TaskDefinitions => Set<TaskDefinitionRow>();
    public DbSet<TaskRunRow> TaskRuns => Set<TaskRunRow>();
    public DbSet<RunEventRow> RunEvents => Set<RunEventRow>();
    public DbSet<ConversationMessageRow> ConversationMessages => Set<ConversationMessageRow>();
    public DbSet<AgentSessionRow> AgentSessions => Set<AgentSessionRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var utcTicks = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        var conversations = model.Entity<ConversationRow>();
        conversations.ToTable("Conversations");
        conversations.HasKey(row => row.Id);
        conversations.Property(row => row.Title).IsRequired();
        conversations.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        conversations.Property(row => row.UpdatedAtUtc).HasConversion(utcTicks);

        var definitions = model.Entity<TaskDefinitionRow>();
        definitions.ToTable("TaskDefinitions");
        definitions.HasKey(row => row.Id);
        definitions.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        definitions.Property(row => row.UpdatedAtUtc).HasConversion(utcTicks);

        var runs = model.Entity<TaskRunRow>();
        runs.ToTable("TaskRuns", table =>
        {
            table.HasCheckConstraint("CK_TaskRuns_RunVersion", "RunVersion >= 0");
            table.HasCheckConstraint("CK_TaskRuns_PayloadSchemaVersion", "PayloadSchemaVersion > 0");
            table.HasCheckConstraint("CK_TaskRuns_Status", "Status IN (" + string.Join(",", Enum.GetNames<RunStatus>().Select(name => $"'{name}'")) + ")");
        });
        runs.HasKey(row => row.Id);
        runs.HasOne<ConversationRow>().WithMany().HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);
        runs.HasOne<TaskDefinitionRow>().WithMany().HasForeignKey(row => row.TaskDefinitionId).OnDelete(DeleteBehavior.Restrict);
        runs.HasIndex(row => new { row.Status, row.CreatedAtUtc });
        runs.Property(row => row.Status).HasConversion<string>();
        runs.Property(row => row.PauseScope).HasConversion<string>();
        runs.Property(row => row.RunVersion).IsConcurrencyToken().ValueGeneratedNever();
        runs.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        runs.Property(row => row.StartedAtUtc).HasConversion(utcTicks);
        runs.Property(row => row.EndedAtUtc).HasConversion(utcTicks);

        var events = model.Entity<RunEventRow>();
        events.ToTable("RunEvents", table => table.HasCheckConstraint("CK_RunEvents_Sequence", "Sequence > 0"));
        events.HasKey(row => row.Id);
        events.HasOne<TaskRunRow>().WithMany().HasForeignKey(row => row.RunId).OnDelete(DeleteBehavior.Restrict);
        events.HasIndex(row => new { row.RunId, row.Sequence }).IsUnique();
        events.Property(row => row.EventType).HasConversion<string>();
        events.Property(row => row.TimestampUtc).HasConversion(utcTicks);

        var messages = model.Entity<ConversationMessageRow>();
        messages.ToTable("ConversationMessages", table =>
        {
            table.HasCheckConstraint("CK_ConversationMessages_Sequence", "Sequence > 0");
            table.HasCheckConstraint("CK_ConversationMessages_Role", "Role IN ('User','Assistant','Tool')");
        });
        messages.HasKey(row => row.Id);
        messages.HasOne<ConversationRow>().WithMany().HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);
        messages.HasOne<TaskRunRow>().WithMany().HasForeignKey(row => row.RunId).OnDelete(DeleteBehavior.Restrict);
        messages.HasIndex(row => new { row.ConversationId, row.Sequence }).IsUnique();
        messages.HasIndex(row => new { row.ConversationId, row.ExternalId }).IsUnique();
        messages.Property(row => row.Role).HasConversion<string>();
        messages.Property(row => row.TimestampUtc).HasConversion(utcTicks);

        var sessions = model.Entity<AgentSessionRow>();
        sessions.ToTable("AgentSessions", table => table.HasCheckConstraint("CK_AgentSessions_SchemaVersion", "SchemaVersion > 0"));
        sessions.HasKey(row => row.Id);
        sessions.HasOne<ConversationRow>().WithMany().HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);
        sessions.Property(row => row.TimestampUtc).HasConversion(utcTicks);
    }
}
