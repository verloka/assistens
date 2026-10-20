using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class AgentContextWindowTests
{
    [Fact]
    public void HandoffBoundsAllTransferredFieldsAndPreservesWholeSourceUrlsAndUnicode()
    {
        var id = Guid.NewGuid();
        var sources = Enumerable.Range(0, 100).Select(index => new SourceLink(new string('t', 500), new("https://jira.example.test/browse/TEST-" + index))).ToArray();
        var context = AgentContextWindow.Bound(new(new string('s', 5000), sources,
            Enumerable.Range(1, 100).Select(index => new ConversationMessage(Guid.NewGuid(), id, index, MessageRole.User,
                new string('a', 999) + "🙂more", DateTimeOffset.UtcNow) { Sources = sources, ErrorCode = new string('e', 1000) }).ToArray())
        { DecisionContext = new string('d', 5000) }, id);
        Assert.Equal(4000, context.Summary!.Length); Assert.Equal(4000, context.DecisionContext!.Length);
        Assert.Equal(10, context.Sources!.Count); Assert.All(context.Sources, source => { Assert.Equal(200, source.Title.Length); Assert.Contains(source.Url, sources.Select(value => value.Url)); });
        Assert.Equal(8, context.Messages!.Count);
        Assert.All(context.Messages, message => { Assert.Equal(999, message.Content.Length); Assert.False(char.IsSurrogate(message.Content[^1])); Assert.Equal(10, message.Sources.Count); Assert.Equal(128, message.ErrorCode!.Length); });
    }

    [Fact]
    public void HandoffExcludesForeignConversationToolsOversizedUrlsAndDuplicateSources()
    {
        var id = Guid.NewGuid(); var source = new SourceLink("TEST-106", new("https://jira.example.test/browse/TEST-106"));
        var context = AgentContextWindow.Bound(new(Sources: [source, source with { Title = "Duplicate" }, new("Oversized", new("https://jira.example.test/" + new string('x', 3000)))],
            Messages: [new(Guid.NewGuid(), id, 1, MessageRole.Tool, "old write arguments", DateTimeOffset.UtcNow), new(Guid.NewGuid(), Guid.NewGuid(), 2, MessageRole.User, "foreign", DateTimeOffset.UtcNow)]), id);
        Assert.Equal(source, Assert.Single(context.Sources!)); Assert.Empty(context.Messages!);
    }

    [Fact]
    public void HandoffDecisionContextIncludesOnlyRecentCompletedRequestsAndOutcomesAndFiltersSecrets()
    {
        var history = Enumerable.Range(0, 20).Select(index => new TaskRun { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Request TEST-" + index,
            ModelProfileId = "fake", Origin = new("test"), CreatedAtUtc = DateTimeOffset.UtcNow, Status = RunStatus.Completed, Summary = "Decision owner notified password=hidden-value " + index }).ToArray();
        var compact = AgentContextWindow.Decisions(history.Append(history[^1] with { Status = RunStatus.Interrupted, Instructions = "unknown-outcome" }));
        Assert.Contains("TEST-19", compact); Assert.Contains("TEST-16", compact); Assert.DoesNotContain("TEST-15", compact);
        Assert.Contains("owner notified", compact); Assert.DoesNotContain("hidden-value", compact); Assert.DoesNotContain("unknown-outcome", compact); Assert.True(compact!.Length <= 4000);
    }

    [Fact]
    public void HandoffNewContextFieldPreservesLegacyJsonWhenAbsent()
    {
        Assert.Equal("{\"Summary\":null,\"Sources\":null,\"Messages\":null}", JsonSerializer.Serialize(new AgentContext()));
        var context = JsonSerializer.Deserialize<AgentContext>("{\"Summary\":\"Legacy TEST-106\",\"Sources\":null,\"Messages\":null}");
        Assert.Equal("Legacy TEST-106", context!.Summary); Assert.Null(context.DecisionContext);
    }
}
