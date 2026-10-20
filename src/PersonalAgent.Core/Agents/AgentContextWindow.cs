using PersonalAgent.Core.Domain;

namespace PersonalAgent.Core.Agents;

// Deterministic compaction of existing history; no additional model call or authority transfer.
public static class AgentContextWindow
{
    public static AgentContext Bound(AgentContext context, Guid conversationId) => new(
        context.Summary is { } summary ? Clip(ConversationPrivacy.Filter(summary), 4000) : null,
        Sources(context.Sources ?? []),
        (context.Messages ?? []).Where(message => message.ConversationId == conversationId
            && message.Role is MessageRole.User or MessageRole.Assistant).TakeLast(8)
            .Select(message => message with
            {
                Content = Clip(ConversationPrivacy.Filter(message.Content), 1000),
                Sources = Sources(message.Sources),
                ErrorCode = message.ErrorCode is { } error ? Clip(ConversationPrivacy.Filter(error), 128) : null
            }).ToArray())
    {
        DecisionContext = context.DecisionContext is { } decisions ? Clip(ConversationPrivacy.Filter(decisions), 4000) : null
    };

    public static string? Decisions(IEnumerable<TaskRun> completed)
    {
        var entries = completed.Where(run => run.Status == RunStatus.Completed).TakeLast(4).Reverse().Select(run =>
            $"Run {run.Id}: Completed\nRequested: {Clip(ConversationPrivacy.Filter(run.Instructions), 250)}\nOutcome: {Clip(ConversationPrivacy.Filter(run.Summary ?? ""), 500)}");
        var text = string.Join("\n\n", entries);
        return text.Length == 0 ? null : Clip(text, 4000);
    }

    public static IReadOnlyList<SourceLink> Sources(IEnumerable<SourceLink> sources) => ConversationPrivacy.FilterSources(sources
        .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Title) && source.Url is { IsAbsoluteUri: true }
            && source.Url.Scheme is "http" or "https" && source.Url.AbsoluteUri.Length <= 2048))
        .DistinctBy(source => source.Url.AbsoluteUri, StringComparer.Ordinal).Take(10)
        .Select(source => source with { Title = Clip(source.Title, 200) }).ToArray();

    private static string Clip(string text, int limit) => text.Length <= limit ? text
        : text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];
}
