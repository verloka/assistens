using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Computer;

namespace PersonalAgent.Channels.Telegram;

// Syntax is transport-specific; task/control behavior and authorization stay in common application handlers.
public sealed class TelegramCommandHandler
{
    private readonly string connectionId;
    private readonly ChannelOwnershipPolicy ownership;
    private readonly ChannelOwnerCommands controls;
    private readonly ChannelMessageRouter router;
    private readonly TaskRunCoordinator runs;
    private readonly string? botUsername;
    private const string Usage = "Commands: /status [run-id], /tasks, /new <task>, /pause <run-id> <version>, /resume <run-id> <version>, /cancel <run-id> <version>. Use /status or /tasks to get the current version.";

    public TelegramCommandHandler(string connectionId, ChannelOwnershipPolicy ownership, ChannelOwnerCommands controls, ChannelMessageRouter router, TaskRunCoordinator runs, string? botUsername = null)
    {
        new ChannelRoute("telegram", connectionId, "configuration").Validate();
        if (botUsername is not null && (botUsername.Length is < 1 or > 64 || botUsername.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new ArgumentException("Invalid configured bot username.");
        this.connectionId = connectionId; this.ownership = ownership; this.controls = controls; this.router = router; this.runs = runs;
        this.botUsername = botUsername;
    }

    public async Task<OutgoingMessage> HandleAsync(IncomingMessage incoming, CancellationToken ct = default)
    {
        var message = incoming.Snapshot();
        if (message.Route.ChannelId != "telegram" || message.Route.ConnectionId != connectionId) throw new ChannelAccessDeniedException();
        await ownership.RequireOwnerAsync(message, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (message.ActionToken is not null || message.Attachments.Count != 0)
            throw new NotSupportedException("Telegram command handler accepts text only; callback actions require explicit authorization.");
        var text = message.Text.Trim();
        if (!text.StartsWith('/')) return await SubmitAsync(message, ct).ConfigureAwait(false);
        var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = split < 0 ? text : text[..split];
        var at = verb.IndexOf('@');
        if (at >= 0)
        {
            if (botUsername is null || !verb[(at + 1)..].Equals(botUsername, StringComparison.OrdinalIgnoreCase)) return Reply(message, Usage);
            verb = verb[..at];
        }
        var arguments = split < 0 ? "" : text[(split + 1)..].Trim();
        if (verb == "/new") return arguments.Length == 0 ? Reply(message, Usage) : await SubmitAsync(message with { Text = arguments }, ct).ConfigureAwait(false);
        if (verb == "/tasks" && arguments.Length == 0)
        {
            var all = runs.GetRuns(); var latest = all.Take(20).Select(Describe);
            return Reply(message, all.Count == 0 ? "No tasks." : $"Tasks: {all.Count}. Latest 20:\n" + string.Join('\n', latest));
        }
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (verb == "/status")
        {
            if (parts.Length == 0)
            {
                var all = runs.GetRuns();
                return Reply(message, "Tasks: " + all.Count + ". " + string.Join(", ", all.GroupBy(run => run.Status).OrderBy(group => group.Key).Select(group => group.Key + ": " + group.Count())));
            }
            if (parts.Length == 1 && Guid.TryParse(parts[0], out var id) && id != Guid.Empty)
                return Reply(message, runs.GetRuns().FirstOrDefault(run => run.Id == id) is { } found ? Describe(found) : "Run not found.");
            return Reply(message, Usage);
        }
        if (verb is not ("/pause" or "/resume" or "/cancel") || parts.Length != 2 || !Guid.TryParse(parts[0], out var runId) || runId == Guid.Empty
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)) return Reply(message, Usage);
        var run = runs.GetRuns().FirstOrDefault(run => run.Id == runId);
        if (run is null) return Reply(message, "Run not found.");
        if (run.RunVersion != version) return Reply(message, "Run changed. Review /status before sending a new control command.");
        if (TaskRunStateMachine.IsTerminal(run.Status)) return Reply(message, Describe(run));
        if (verb == "/pause" && run.PauseScope == PauseScope.GuiOnly)
            return Reply(message, "Run already has a GUI-only pause. Review /status and resume before changing pause scope.");
        if (verb == "/pause" && run.Status == RunStatus.Blocked)
            return Reply(message, "Run is blocked. Review its state in Personal Agent.");
        if (verb == "/resume" && run.Status is not (RunStatus.Paused or RunStatus.PausedForUser or RunStatus.Running) && run.PauseScope != PauseScope.GuiOnly)
            return Reply(message, "Run is not ready to resume. Review /status.");
        try
        {
            var commandId = Identity(message, "command");
            var result = verb switch
            {
                "/pause" => await controls.PauseAsync(message, new(commandId, runId, version), ct).ConfigureAwait(false),
                "/resume" => await controls.ResumeAsync(message, new(commandId, runId, version), ct).ConfigureAwait(false),
                _ => await controls.CancelAsync(message, new(commandId, runId, version), ct).ConfigureAwait(false)
            };
            return Reply(message, Describe(result), result);
        }
        catch (RunVersionConflictException) { return Reply(message, "Run changed. Review /status before sending a new control command."); }
        catch (DesktopAccessException) { return Reply(message, "Desktop access is blocked. Review Personal Agent before resuming."); }
    }

    private async Task<OutgoingMessage> SubmitAsync(IncomingMessage message, CancellationToken ct)
    {
        var run = await router.SubmitAsync(message, ct).ConfigureAwait(false);
        return Reply(message, "Task accepted. " + Describe(run), run);
    }
    private static string Describe(TaskRun run) => $"{run.Id:D} | {run.Status} | version {run.RunVersion.ToString(CultureInfo.InvariantCulture)}";
    private static OutgoingMessage Reply(IncomingMessage message, string text, TaskRun? run = null) => new OutgoingMessage
    {
        Id = Identity(message, "reply"), Destination = message.Route, Text = text, RunId = run?.Id, ConversationId = run?.ConversationId
    }.Snapshot();
    private static Guid Identity(IncomingMessage message, string purpose) => new(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { message.Route, message.SenderId, message.ExternalMessageId, Purpose = purpose }))).AsSpan(0, 16));
}
