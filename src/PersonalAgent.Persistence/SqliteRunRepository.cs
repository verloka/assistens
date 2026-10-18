using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteRunRepository(IDbContextFactory<AgentDbContext> contexts) : IRunRepository
{
    public async Task CreateAsync(TaskRun run, Conversation conversation, AgentRequest request,
        IReadOnlyList<RunEvent> initialEvents, CancellationToken cancellationToken = default)
    {
        if (run.Id == Guid.Empty || conversation.Id == Guid.Empty || conversation.Id != run.ConversationId || request.RunId != run.Id || request.ConversationId != run.ConversationId)
            throw new ArgumentException("Run/conversation/request identity mismatch.");
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.ModelProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Origin.Source);
        if (run.Status is not (RunStatus.Created or RunStatus.Queued) || run.StartedAtUtc is not null || run.EndedAtUtc is not null ||
            run.RunVersion != (run.Status == RunStatus.Created ? 0 : 1))
            throw new ArgumentException("Only a new unstarted attempt can be created.");
        if (request.Instructions != run.Instructions || request.ModelProfileId != run.ModelProfileId)
            throw new ArgumentException("Run/request payload mismatch.");
        request.Limits.Validate();
        ValidateEvents(run.Id, initialEvents, 0);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Conversations.AnyAsync(row => row.Id == conversation.Id, cancellationToken))
            db.Conversations.Add(new ConversationRow
            {
                Id = conversation.Id, Title = conversation.Title, CreatedAtUtc = conversation.CreatedAtUtc, UpdatedAtUtc = conversation.UpdatedAtUtc
            });
        var row = new TaskRunRow
        {
            Id = run.Id, TaskDefinitionId = run.TaskDefinitionId, ConversationId = run.ConversationId,
            OriginJson = JsonSerializer.Serialize(run.Origin), Instructions = run.Instructions, ModelProfileId = run.ModelProfileId,
            CreatedAtUtc = run.CreatedAtUtc, RequestJson = JsonSerializer.Serialize(request), PayloadSchemaVersion = 1
        };
        Apply(row, run, null);
        db.TaskRuns.Add(row);
        db.RunEvents.AddRange(initialEvents.Select(ToRow));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<StoredRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // Snapshot and event history must describe the same committed revision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.TaskRuns.AsNoTracking().SingleOrDefaultAsync(value => value.Id == runId, cancellationToken);
        if (row is null) return null;
        CheckSchema(row);
        var events = await db.RunEvents.AsNoTracking().Where(value => value.RunId == runId).OrderBy(value => value.Sequence).ToListAsync(cancellationToken);
        var result = new StoredRun(ToDomain(row), Read<AgentRequest>(row.RequestJson), events.Select(ToDomain).ToArray(),
            row.ResultJson is null ? null : Read<AgentRunResult>(row.ResultJson));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task UpdateAsync(TaskRun run, long expectedVersion, IReadOnlyList<RunEvent> newEvents,
        AgentRunResult? result = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // Read the run revision and its last event in one SQLite statement/snapshot.
        var revision = await db.TaskRuns.Where(value => value.Id == run.Id).Select(value => new
        {
            Row = value,
            LastSequence = db.RunEvents.Where(evt => evt.RunId == value.Id).Max(evt => (long?)evt.Sequence) ?? 0
        }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Unknown run {run.Id}.");
        var row = revision.Row;
        CheckSchema(row);
        if (row.RunVersion != expectedVersion) throw new RunVersionConflictException(run.Id, expectedVersion, row.RunVersion);
        var previous = ToDomain(row);
        if (TaskRunStateMachine.IsTerminal(previous.Status)) throw new InvalidOperationException("A terminal attempt cannot be rewritten.");
        if (run.RunVersion != checked(expectedVersion + 1)) throw new ArgumentException("Every committed update advances RunVersion once.");
        if (run.PauseScope is { } scope && !Enum.IsDefined(scope)) throw new ArgumentException("Unsupported pause scope.");
        if (run.ConversationId != previous.ConversationId || run.TaskDefinitionId != previous.TaskDefinitionId || run.Origin != previous.Origin ||
            run.Instructions != previous.Instructions || run.ModelProfileId != previous.ModelProfileId || run.CreatedAtUtc != previous.CreatedAtUtc)
            throw new ArgumentException("An update cannot replace attempt identity or input.");
        if (run.Status != previous.Status)
        {
            var timestamp = run.EndedAtUtc ?? (previous.StartedAtUtc is null ? run.StartedAtUtc : null) ?? newEvents.LastOrDefault()?.TimestampUtc ?? DateTimeOffset.UtcNow;
            var transition = TaskRunStateMachine.Transition(previous, run.Status, expectedVersion, timestamp, run.Reason, run.PauseScope);
            if (run.StartedAtUtc != transition.StartedAtUtc || run.EndedAtUtc != transition.EndedAtUtc || run.PauseScope != transition.PauseScope)
                throw new ArgumentException("Snapshot timestamps/pause scope disagree with transition.");
        }
        else if (run.StartedAtUtc != previous.StartedAtUtc || run.EndedAtUtc != previous.EndedAtUtc)
            throw new ArgumentException("Progress updates cannot rewrite lifecycle timestamps.");
        ValidateEvents(run.Id, newEvents, revision.LastSequence);
        if (run.Status == RunStatus.Completed && result is null) throw new ArgumentException("Completion requires a structured result.");
        if (result is not null && (run.Summary != result.Summary || run.ErrorCode != result.ErrorCode || ResultStatus(result.Status) != run.Status || !run.Sources.SequenceEqual(result.Sources)))
            throw new ArgumentException("Final result must match the run snapshot.");
        Apply(row, run, result);
        db.RunEvents.AddRange(newEvents.Select(ToRow));
        try
        {
            // EF wraps row CAS + all event inserts in one transaction. No external action/retry occurs here.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await using var check = await contexts.CreateDbContextAsync(cancellationToken);
            var actual = await check.TaskRuns.Where(value => value.Id == run.Id).Select(value => value.RunVersion).SingleAsync(cancellationToken);
            if (actual != expectedVersion) throw new RunVersionConflictException(run.Id, expectedVersion, actual);
            throw;
        }
    }

    public async Task<IReadOnlyList<TaskRun>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.TaskRuns.AsNoTracking().OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).ToListAsync(cancellationToken);
        foreach (var row in rows) CheckSchema(row);
        return rows.Select(ToDomain).ToArray();
    }

    private static void ValidateEvents(Guid runId, IReadOnlyList<RunEvent> events, long last)
    {
        if (events.Count == 0) throw new ArgumentException("A state write requires appended history.");
        foreach (var value in events)
        {
            if (value.Id == Guid.Empty || value.RunId != runId || value.Sequence != ++last)
                throw new ArgumentException("Events must have IDs, belong to the attempt and append contiguous sequences.");
        }
    }
    private static void CheckSchema(TaskRunRow row)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException($"Unsupported run payload schema {row.PayloadSchemaVersion}.");
    }
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Missing stored payload.");
    private static RunStatus ResultStatus(AgentResultStatus status) => status switch
    {
        AgentResultStatus.Completed => RunStatus.Completed, AgentResultStatus.Cancelled => RunStatus.Cancelled,
        AgentResultStatus.Failed => RunStatus.Failed, AgentResultStatus.Blocked => RunStatus.Blocked,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
    private static void Apply(TaskRunRow row, TaskRun run, AgentRunResult? result)
    {
        row.Status = run.Status; row.RunVersion = run.RunVersion; row.PauseScope = run.PauseScope; row.Reason = run.Reason;
        row.StartedAtUtc = run.StartedAtUtc; row.EndedAtUtc = run.EndedAtUtc; row.Summary = run.Summary; row.ErrorCode = run.ErrorCode;
        row.SourcesJson = JsonSerializer.Serialize(run.Sources);
        row.ResultJson = result is null ? null : JsonSerializer.Serialize(result);
    }
    private static TaskRun ToDomain(TaskRunRow row) => new()
    {
        Id = row.Id, TaskDefinitionId = row.TaskDefinitionId, ConversationId = row.ConversationId,
        Origin = Read<TaskOrigin>(row.OriginJson), Instructions = row.Instructions, ModelProfileId = row.ModelProfileId,
        Status = row.Status, RunVersion = row.RunVersion, PauseScope = row.PauseScope, Reason = row.Reason,
        CreatedAtUtc = row.CreatedAtUtc, StartedAtUtc = row.StartedAtUtc, EndedAtUtc = row.EndedAtUtc,
        Summary = row.Summary, ErrorCode = row.ErrorCode, Sources = Read<SourceLink[]>(row.SourcesJson)
    };
    private static RunEventRow ToRow(RunEvent value) => new()
    {
        Id = value.Id, RunId = value.RunId, Sequence = value.Sequence, EventType = value.EventType, TimestampUtc = value.TimestampUtc,
        Message = value.Message, ToolId = value.ToolId, ToolCallId = value.ToolCallId, PayloadJson = value.Payload?.GetRawText()
    };
    private static RunEvent ToDomain(RunEventRow row) => new()
    {
        Id = row.Id, RunId = row.RunId, Sequence = row.Sequence, EventType = row.EventType, TimestampUtc = row.TimestampUtc,
        Message = row.Message, ToolId = row.ToolId, ToolCallId = row.ToolCallId,
        Payload = row.PayloadJson is null ? null : JsonSerializer.Deserialize<JsonElement>(row.PayloadJson)
    };
}
