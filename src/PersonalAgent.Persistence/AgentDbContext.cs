using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Security;
using PersonalAgent.Core.Channels;
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
    public DbSet<ModelProfileRow> ModelProfiles => Set<ModelProfileRow>();
    public DbSet<McpConnectionRow> McpConnections => Set<McpConnectionRow>();
    public DbSet<ApprovalRequestRow> ApprovalRequests => Set<ApprovalRequestRow>();
    public DbSet<ToolCallRow> ToolCalls => Set<ToolCallRow>();
    public DbSet<ScheduleRow> Schedules => Set<ScheduleRow>();
    public DbSet<ChannelBindingRow> ChannelBindings => Set<ChannelBindingRow>();
    public DbSet<ChannelCursorRow> ChannelCursors => Set<ChannelCursorRow>();
    public DbSet<ChannelActionRow> ChannelActions => Set<ChannelActionRow>();
    public DbSet<NotificationOutboxRow> NotificationOutbox => Set<NotificationOutboxRow>();
    public DbSet<ChannelConfigurationRow> ChannelConfigurations => Set<ChannelConfigurationRow>();
    public DbSet<ChannelConversationLinkRow> ChannelConversationLinks => Set<ChannelConversationLinkRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var channelConfigurations = model.Entity<ChannelConfigurationRow>();
        channelConfigurations.ToTable("ChannelConfigurations");
        channelConfigurations.HasKey(row => row.Id);
        channelConfigurations.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
        channelConfigurations.HasIndex(row => new { row.OwnerSource, row.OwnerId, row.ChannelId, row.ConnectionId, row.DestinationId }).IsUnique();
        channelConfigurations.HasIndex(row => new { row.ChannelId, row.RemoteIdentity }).IsUnique().HasFilter("\"Enabled\" = 1 AND \"RemoteIdentity\" IS NOT NULL");
        var channelActions = model.Entity<ChannelActionRow>();
        channelActions.ToTable("ChannelActions");
        channelActions.HasKey(row => row.TokenHash);
        channelActions.Property(row => row.ScopeJson).IsRequired();
        var cursors = model.Entity<ChannelCursorRow>();
        cursors.ToTable("ChannelCursors", table => table.HasCheckConstraint("CK_ChannelCursors_Position", "NextSequence >= 0 AND Version >= 0 AND PayloadSchemaVersion > 0"));
        cursors.HasKey(row => new { row.ChannelId, row.ConnectionId });
        cursors.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
        var profiles = model.Entity<ModelProfileRow>();
        profiles.ToTable("ModelProfiles");
        profiles.HasKey(row => row.Id);
        profiles.Property(row => row.ProfileJson).IsRequired();
        var connections = model.Entity<McpConnectionRow>();
        connections.ToTable("McpConnections");
        connections.HasKey(row => row.Id);
        connections.Property(row => row.ProfileJson).IsRequired();
        var utcTicks = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        var conversationLinks = model.Entity<ChannelConversationLinkRow>();
        conversationLinks.ToTable("ChannelConversationLinks");
        conversationLinks.HasKey(row => new { row.OwnerSource, row.OwnerId, row.ChannelId, row.ConnectionId, row.DestinationId, row.SenderId, row.ExternalMessageId });
        conversationLinks.HasIndex(row => new { row.OwnerSource, row.OwnerId, row.ChannelId, row.ConnectionId, row.DestinationId, row.SenderId, row.ExternalUpdateId })
            .IsUnique().HasFilter("\"ExternalUpdateId\" IS NOT NULL");
        conversationLinks.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        conversationLinks.HasOne<ConversationRow>().WithMany().HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);
        conversationLinks.HasOne<TaskRunRow>().WithMany().HasForeignKey(row => row.RunId).OnDelete(DeleteBehavior.Restrict);
        var outbox = model.Entity<NotificationOutboxRow>();
        outbox.ToTable("NotificationOutbox", table =>
        {
            table.HasCheckConstraint("CK_NotificationOutbox_Version", "Attempts >= 0 AND Version >= 0 AND PayloadSchemaVersion > 0");
            table.HasCheckConstraint("CK_NotificationOutbox_Status", "Status IN ('Pending','Sending','Delivered','Failed','OutcomeUnknown')");
        });
        outbox.HasKey(row => row.Id);
        outbox.HasIndex(row => new { row.EventId, row.ChannelId, row.ConnectionId, row.DestinationId }).IsUnique();
        outbox.HasIndex(row => new { row.Status, row.NextAttemptAtUtc });
        outbox.Property(row => row.Status).HasConversion<string>();
        outbox.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
        outbox.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        outbox.Property(row => row.UpdatedAtUtc).HasConversion(utcTicks);
        outbox.Property(row => row.NextAttemptAtUtc).HasConversion(utcTicks);
        outbox.Property(row => row.DeliveredAtUtc).HasConversion(utcTicks);
        var bindings = model.Entity<ChannelBindingRow>();
        bindings.ToTable("ChannelBindings", table =>
        {
            table.HasCheckConstraint("CK_ChannelBindings_Version", "Version >= 0 AND PayloadSchemaVersion > 0");
            table.HasCheckConstraint("CK_ChannelBindings_Status", "Status IN ('Pending','Approved','Revoked')");
        });
        bindings.HasKey(row => row.Id);
        bindings.HasIndex(row => new { row.ChannelId, row.ConnectionId, row.DestinationId, row.SenderId }).IsUnique();
        bindings.HasIndex(row => new { row.OwnerSource, row.OwnerId, row.Status });
        bindings.Property(row => row.Status).HasConversion<string>();
        bindings.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
        bindings.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        bindings.Property(row => row.UpdatedAtUtc).HasConversion(utcTicks);
        var schedules = model.Entity<ScheduleRow>();
        schedules.ToTable("Schedules", table =>
        {
            table.HasCheckConstraint("CK_Schedules_Version", "Version >= 0 AND PayloadSchemaVersion > 0");
            table.HasCheckConstraint("CK_Schedules_Mode", "Mode IN ('Once','Interval','Daily','Weekdays')");
            table.HasCheckConstraint("CK_Schedules_Misfire", "MisfirePolicy IN ('RunOnce','Skip','CatchUp') AND MaxCatchUpRuns BETWEEN 1 AND 10");
            table.HasCheckConstraint("CK_Schedules_DesktopPolicy", "DesktopPolicy IN ('StartWhenIdle','StartAfterCountdown','AskEveryTime')");
        });
        schedules.HasKey(row => row.Id);
        schedules.HasOne<TaskDefinitionRow>().WithMany().HasForeignKey(row => row.TaskDefinitionId).OnDelete(DeleteBehavior.Restrict);
        schedules.HasIndex(row => new { row.Enabled, row.NextRunAtUtc });
        schedules.Property(row => row.Mode).HasConversion<string>();
        schedules.Property(row => row.MisfirePolicy).HasConversion<string>().HasDefaultValue(MisfirePolicy.RunOnce);
        schedules.Property(row => row.DesktopPolicy).HasConversion<string>().HasDefaultValue(ScheduledDesktopPolicy.StartWhenIdle);
        schedules.Property(row => row.MaxCatchUpRuns).HasDefaultValue(3);
        schedules.Property(row => row.CatchUpUntilUtc).HasConversion(utcTicks);
        schedules.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
        schedules.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        schedules.Property(row => row.UpdatedAtUtc).HasConversion(utcTicks);
        schedules.Property(row => row.NextRunAtUtc).HasConversion(utcTicks);
        var calls = model.Entity<ToolCallRow>();
        calls.ToTable("ToolCalls", table =>
        {
            table.HasCheckConstraint("CK_ToolCalls_SchemaVersion", "PayloadSchemaVersion > 0");
            table.HasCheckConstraint("CK_ToolCalls_Status", "Status IN ('Dispatched','Acknowledged','OutcomeUnknown')");
        });
        calls.HasKey(row => row.Id);
        calls.HasOne<TaskRunRow>().WithMany().HasForeignKey(row => row.RunId).OnDelete(DeleteBehavior.Restrict);
        calls.HasOne<ApprovalRequestRow>().WithMany().HasForeignKey(row => row.ApprovalRequestId).OnDelete(DeleteBehavior.Restrict);
        calls.HasIndex(row => new { row.RunId, row.ToolCallId }).IsUnique();
        calls.HasIndex(row => row.ApprovalRequestId).IsUnique();
        calls.Property(row => row.Status).HasConversion<string>();
        calls.Property(row => row.DispatchedAtUtc).HasConversion(utcTicks);
        calls.Property(row => row.FinishedAtUtc).HasConversion(utcTicks);
        var approvals = model.Entity<ApprovalRequestRow>();
        approvals.ToTable("ApprovalRequests", table =>
        {
            table.HasCheckConstraint("CK_ApprovalRequests_Version", "Version >= 0 AND RunVersion >= 0 AND PayloadSchemaVersion > 0");
            table.HasCheckConstraint("CK_ApprovalRequests_Expiry", "ExpiresAtUtc > CreatedAtUtc");
            table.HasCheckConstraint("CK_ApprovalRequests_Status", "Status IN ('Pending','Approved','Rejected','Expired')");
        });
        approvals.HasKey(row => row.Id);
        approvals.HasOne<TaskRunRow>().WithMany().HasForeignKey(row => row.RunId).OnDelete(DeleteBehavior.Restrict);
        approvals.HasIndex(row => new { row.RunId, row.ToolCallId }).IsUnique();
        approvals.HasIndex(row => new { row.OwnerSource, row.OwnerId, row.Status, row.ExpiresAtUtc });
        approvals.Property(row => row.Status).HasConversion<string>();
        approvals.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
        approvals.Property(row => row.CreatedAtUtc).HasConversion(utcTicks);
        approvals.Property(row => row.ExpiresAtUtc).HasConversion(utcTicks);
        approvals.Property(row => row.DecidedAtUtc).HasConversion(utcTicks);
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
        runs.HasIndex(row => new { row.SubmissionSource, row.SubmissionKey }).IsUnique();
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
        messages.Property(row => row.SourcesJson).HasDefaultValue("[]");

        var sessions = model.Entity<AgentSessionRow>();
        sessions.ToTable("AgentSessions", table => table.HasCheckConstraint("CK_AgentSessions_SchemaVersion", "SchemaVersion > 0"));
        sessions.HasKey(row => row.Id);
        sessions.HasOne<ConversationRow>().WithMany().HasForeignKey(row => row.ConversationId).OnDelete(DeleteBehavior.Restrict);
        sessions.Property(row => row.TimestampUtc).HasConversion(utcTicks);
    }
}
