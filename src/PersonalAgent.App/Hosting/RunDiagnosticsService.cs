using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Diagnostics;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PersonalAgent.Persistence;
using PersonalAgent.Core.Channels;

namespace PersonalAgent.App;

public enum RunDiagnosticCode { None, HistoryCleared, UserControl, UserActivity, DesktopChanged, WaitingForDesktop,
    WaitingForApproval, InputCleanupUncertain, OutcomeUnknown, PermissionDenied, LimitExceeded, ModelConfiguration,
    DeferredExpired, Interrupted, Failure, Cancelled }

public sealed record RunDiagnostic(Guid RunId, Guid ConversationId, RunStatus Status, RunDiagnosticCode Code, string NextStep,
    string Profile, TimeSpan? Duration, int ToolRequests, int ToolCompletions, int Resumes, string Usage, long? DesktopLeaseGeneration,
    IReadOnlyList<RunDiagnosticEvent> Events);
public sealed record RunDiagnosticEvent(long Sequence, RunEventType Kind, DateTimeOffset TimestampUtc, Guid? ToolCallId, string? Tool);
public sealed record DeliveryDiagnostic(Guid DeliveryId, Guid? RunId, NotificationDeliveryStatus Status, int Attempts, string NextStep);

public sealed class RunDiagnosticsService(TaskRunCoordinator runs, IDesktopAccessCoordinator desktop, ILogger<RunDiagnosticsService> logger,
    IRunRepository? repository = null, NotificationDispatcher? notifications = null, PersonalAgent.Mcp.McpConnectionManager? mcp = null,
    SqliteAgentDbContextFactory? contexts = null) : BackgroundService
{
    private DeliveryDiagnostic[] deliveryProblems = [];
    public IReadOnlyList<DeliveryDiagnostic> DeliveryProblems => Volatile.Read(ref deliveryProblems);
    private readonly Dictionary<Guid, long> observed = [];
    private readonly SemaphoreSlim serial = new(1, 1);
    public override Task StartAsync(CancellationToken ct)
    {
        // Durable history is visible in UI, but never recounted as new process execution.
        foreach (var run in runs.GetRuns()) observed[run.Id] = runs.GetEvents(run.Id).LastOrDefault()?.Sequence ?? 0;
        if (notifications is not null) notifications.Attempted += OnNotification;
        return base.StartAsync(ct);
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        if (notifications is not null) notifications.Attempted -= OnNotification;
        await base.StopAsync(ct);
    }
    private void OnNotification(NotificationAttempt attempt) => logger.LogInformation(new EventId(2200),
        "NotificationAttempted {NotificationId} {RunId} {ConversationId} {Attempt} {Status}", attempt.NotificationId, attempt.RunId, attempt.ConversationId, attempt.Attempt, attempt.Status);

    public async Task ObserveAsync(CancellationToken ct = default)
    {
        await serial.WaitAsync(ct);
        try
        {
            foreach (var id in runs.GetRuns().Select(run => run.Id))
            {
                var (run, events, result) = runs.GetSnapshot(id);
                if (run.ContentClearedAtUtc is not null) continue;
                var last = observed.GetValueOrDefault(run.Id);
                if (events.All(evt => evt.Sequence <= last)) continue;
                var profile = repository is null ? null : (await repository.GetAsync(run.Id, ct))?.Request.Profile?.ProviderId;
                foreach (var evt in events.Where(evt => evt.Sequence > last))
                {
                    var row = Describe(run, events, desktop.Snapshot, result);
                    var connection = mcp?.List().FirstOrDefault(item => item.Tools.Any(tool => tool.Descriptor.Id == evt.ToolId))?.Id;
                    var eventStatus = TransitionStatus(evt) ?? run.Status;
                    var generation = evt.EventType == RunEventType.DesktopPauseConfirmed && evt.Payload is { ValueKind: System.Text.Json.JsonValueKind.Object } payload
                        && payload.TryGetProperty("Generation", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
                        && value.TryGetInt64(out var number) ? (long?)number : null;
                    logger.LogInformation(new EventId(2100 + (int)evt.EventType),
                        "Run diagnostic {RunId} {ConversationId} {ToolCallId} {ToolId} {ProviderId} {McpConnectionId} {Duration} {Status} {ErrorCode} {DesktopLeaseGeneration}",
                        run.Id, run.ConversationId, evt.ToolCallId, evt.ToolId, profile, connection, row.Duration, eventStatus, row.Code, generation);
                    if (evt.EventType == RunEventType.RunResumed) AgentTelemetry.Record(AgentOperation.Resume);
                    if (evt.EventType == RunEventType.RunFinished || evt.EventType == RunEventType.StatusChanged && eventStatus == RunStatus.Blocked && result is not null)
                    {
                        var outcome = eventStatus == RunStatus.Completed ? AgentOperationOutcome.Success
                            : eventStatus == RunStatus.Cancelled ? AgentOperationOutcome.Cancelled
                            : eventStatus is RunStatus.Skipped or RunStatus.Blocked ? AgentOperationOutcome.Blocked : AgentOperationOutcome.Failed;
                        AgentTelemetry.Record(AgentOperation.Run, outcome, row.Duration);
                        if (eventStatus is RunStatus.Failed or RunStatus.Interrupted or RunStatus.Blocked
                            && events.Any(prior => prior.EventType == RunEventType.RunResumed && prior.Sequence < evt.Sequence))
                            AgentTelemetry.Record(AgentOperation.FailureAfterResume, AgentOperationOutcome.Failed);
                        if (result?.Usage is { } usage)
                            AgentTelemetry.Usage(usage.InputTokens, usage.OutputTokens, usage.Cost, usage.Currency);
                    }
                    observed[run.Id] = evt.Sequence;
                }
            }
            if (contexts is not null)
            {
                await using var db = contexts.CreateDbContext();
                // Select metadata only; message JSON, route names and provider errors stay out.
                var outstanding = await (from delivery in db.NotificationOutbox.AsNoTracking()
                    where delivery.Status != NotificationDeliveryStatus.Delivered
                    join evt in db.RunEvents on delivery.EventId equals evt.Id into matching
                    from evt in matching.DefaultIfEmpty()
                    orderby delivery.CreatedAtUtc descending
                    select new { delivery.Id, RunId = evt == null ? (Guid?)null : evt.RunId, delivery.Status, delivery.Attempts })
                    .Take(100).ToArrayAsync(ct);
                Volatile.Write(ref deliveryProblems, outstanding.Select(row => new DeliveryDiagnostic(row.Id, row.RunId, row.Status, row.Attempts,
                    row.Status == NotificationDeliveryStatus.OutcomeUnknown ? "Check actual delivery before any new send. Do not retry this delivery."
                    : row.Status == NotificationDeliveryStatus.Failed ? "Review channel configuration and actual delivered parts before any new send."
                    : "Delivery is pending or claimed; do not manually replay it.")).ToArray());
            }
        }
        finally { serial.Release(); }
    }

    public IReadOnlyList<RunDiagnostic> Snapshot() => runs.GetRuns().Select(run =>
    {
        var snapshot = runs.GetSnapshot(run.Id);
        return Describe(snapshot.Run, snapshot.Events, desktop.Snapshot, snapshot.Result);
    }).ToArray();

    public static RunDiagnostic Describe(TaskRun run, IReadOnlyList<RunEvent> events, DesktopAccessSnapshot desktop, Core.Agents.AgentRunResult? result)
    {
        var code = run.ContentClearedAtUtc is not null ? RunDiagnosticCode.HistoryCleared
            : run.ErrorCode switch
            {
                "InputCleanupUncertain" => RunDiagnosticCode.InputCleanupUncertain,
                "ToolOutcomeUnknown" or "PostActionObservationFailed" or "DeliveryOutcomeUnknown" => RunDiagnosticCode.OutcomeUnknown,
                "ToolPermissionDenied" or "ToolNotAllowed" or "ToolDispatchDenied" or "DestinationDenied" => RunDiagnosticCode.PermissionDenied,
                "StepLimitExceeded" or "TokenLimitExceeded" or "RepeatedActionLimitExceeded" or "RunTimedOut" => RunDiagnosticCode.LimitExceeded,
                "ProviderAuthenticationFailed" or "ModelAuthenticationFailed" or "ModelProfileMismatch" or "ModelCapabilitiesUnavailable" or "ModelNotConfigured" => RunDiagnosticCode.ModelConfiguration,
                "DeferredExpired" => RunDiagnosticCode.DeferredExpired,
                _ => run.Status switch
                {
                    RunStatus.Interrupted => RunDiagnosticCode.Interrupted,
                    RunStatus.Failed or RunStatus.Blocked => RunDiagnosticCode.Failure,
                    RunStatus.WaitingForApproval => RunDiagnosticCode.WaitingForApproval,
                    RunStatus.WaitingForDesktop => RunDiagnosticCode.WaitingForDesktop,
                    RunStatus.Cancelled => RunDiagnosticCode.Cancelled,
                    _ when run.PauseScope is not null => run.Reason switch
                    { nameof(PauseReason.AutoPause) => RunDiagnosticCode.UserActivity, nameof(PauseReason.DesktopEnvironmentChanged) => RunDiagnosticCode.DesktopChanged, _ => RunDiagnosticCode.UserControl },
                    _ => RunDiagnosticCode.None
                }
            };
        if (desktop.InputCleanupUncertain && code != RunDiagnosticCode.HistoryCleared) code = RunDiagnosticCode.InputCleanupUncertain;
        var next = code switch
        {
            RunDiagnosticCode.InputCleanupUncertain => "Keep desktop actions blocked. Verify released keys/buttons and review the input failure; Resume cannot clear it.",
            RunDiagnosticCode.OutcomeUnknown or RunDiagnosticCode.Interrupted => "Check the actual external outcome before any new action. Do not replay this attempt.",
            RunDiagnosticCode.UserActivity or RunDiagnosticCode.UserControl => "Human control has priority. Wait for safe pause confirmation, then explicitly Resume when ready; a fresh capture is required.",
            RunDiagnosticCode.DesktopChanged => "Check session/display availability, then explicitly Resume with a fresh capture.",
            RunDiagnosticCode.PermissionDenied => "Review the current tool/destination allowlist and exact approval. Old consent cannot authorize a retry.",
            RunDiagnosticCode.WaitingForApproval => "Review the exact pending action in Approvals. Diagnostics cannot approve it.",
            RunDiagnosticCode.WaitingForDesktop => "Review Desktop requests; continue working or explicitly choose Start, Skip or Defer.",
            RunDiagnosticCode.ModelConfiguration => "Review the saved model profile and run its explicit connectivity/capability test.",
            RunDiagnosticCode.LimitExceeded => "Review limits and actual completed effects before submitting a new task.",
            RunDiagnosticCode.Failure => "Review model/tool configuration and actual effects. Raw errors are omitted; no automatic retry is offered.",
            RunDiagnosticCode.DeferredExpired => "The desktop wait expired; this occurrence was skipped. Review schedule policy for future occurrences.",
            RunDiagnosticCode.HistoryCleared => "Private history was cleared. Safety and duplicate-action records remain.",
            _ => "No diagnostic action required."
        };
        var duration = run.StartedAtUtc is { } start && run.EndedAtUtc is { } end && end >= start ? (TimeSpan?)(end - start) : null;
        var usage = result?.Usage is { } known ? $"Input: {known.InputTokens}; output: {known.OutputTokens}; cost: {(known.Cost is { } cost ? cost.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (known.Currency is "USD" or "EUR" or "GBP" ? known.Currency : "Other") : "unknown")}" : "Usage and cost: unknown";
        return new(run.Id, run.ConversationId, run.Status, code, next, Identifier(run.ModelProfileId), duration,
            events.Count(evt => evt.EventType == RunEventType.ToolRequested), events.Count(evt => evt.EventType == RunEventType.ToolExecuted),
            events.Count(evt => evt.EventType == RunEventType.RunResumed), usage,
            desktop.OwnerRunId == run.Id || desktop.HumanControlOwnerRunId == run.Id ? desktop.Generation : null,
            events.Select(evt => new RunDiagnosticEvent(evt.Sequence, evt.EventType, evt.TimestampUtc, evt.ToolCallId,
                evt.ToolId is { } toolId ? Identifier(toolId) : null)).ToArray());
    }
    public static string Identifier(string value) => "id:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static RunStatus? TransitionStatus(RunEvent evt) => evt.Payload is { ValueKind: System.Text.Json.JsonValueKind.Object } payload
        && payload.TryGetProperty("Status", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
        && Enum.TryParse<RunStatus>(value.GetString(), out var status) && Enum.IsDefined(status) ? status : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                try { await ObserveAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) { logger.LogWarning(new EventId(2199), error, "Diagnostic observation unavailable."); }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
