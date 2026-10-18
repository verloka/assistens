using PersonalAgent.Core.Domain;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class DomainSerializationTests
{
    [Fact]
    public void DomainGraphRoundTripsWithOnlySystemTextJsonAndPreservesLinks()
    {
        var now = DateTimeOffset.Parse("2026-10-18T09:00:00Z");
        var conversation = new Conversation { Id = Guid.NewGuid(), Title = "Diagnostic", CreatedAtUtc = now, UpdatedAtUtc = now };
        var definition = new TaskDefinition
        {
            Id = Guid.NewGuid(), Name = "Read test", Instructions = "Read a local value", ModelProfileId = "fake",
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var run = new TaskRun
        {
            Id = Guid.NewGuid(), TaskDefinitionId = definition.Id, ConversationId = conversation.Id,
            Origin = new TaskOrigin("test-channel", "owner", "message-1"), Instructions = definition.Instructions,
            ModelProfileId = definition.ModelProfileId, Status = RunStatus.Completed, CreatedAtUtc = now,
            StartedAtUtc = now.AddSeconds(1), EndedAtUtc = now.AddSeconds(2), RunVersion = 3,
            Summary = "Read succeeded", Sources = [new SourceLink("Test source", new Uri("https://example.com/source"))]
        };
        var step = new RunEvent
        {
            Id = Guid.NewGuid(), RunId = run.Id, Sequence = 1, EventType = RunEventType.Progress,
            TimestampUtc = now, Message = "Read value", ToolId = "test.read", ToolCallId = Guid.NewGuid(),
            Payload = JsonSerializer.SerializeToElement(new { value = 42 })
        };

        Assert.Equal(definition, RoundTrip(definition));
        Assert.Equal(conversation, RoundTrip(conversation));
        var restored = RoundTrip(run);
        Assert.Equal(run.Id, restored.Id);
        Assert.Equal(definition.Id, restored.TaskDefinitionId);
        Assert.Equal(conversation.Id, restored.ConversationId);
        Assert.Equal(run.Origin, restored.Origin);
        Assert.Equal(run.Status, restored.Status);
        Assert.Equal(run.RunVersion, restored.RunVersion);
        Assert.Equal(run.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(run.StartedAtUtc, restored.StartedAtUtc);
        Assert.Equal(run.EndedAtUtc, restored.EndedAtUtc);
        Assert.Equal(run.Summary, restored.Summary);
        Assert.Equal(run.Sources, restored.Sources);
        var restoredEvent = RoundTrip(step);
        Assert.Equal(run.Id, restoredEvent.RunId);
        Assert.Equal(step.ToolCallId, restoredEvent.ToolCallId);
        Assert.Equal(42, restoredEvent.Payload!.Value.GetProperty("value").GetInt32());
        Assert.Contains("\"Completed\"", JsonSerializer.Serialize(run));
    }

    [Fact]
    public void CoreHasNoSdkOrInfrastructureAssemblyReferences()
    {
        var references = typeof(TaskRun).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.True(reference.Name == "System.Runtime" || reference.Name!.StartsWith("System."), reference.FullName));
        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("PersonalAgent."));
    }

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
