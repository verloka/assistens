using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class ApprovalRequestTests
{
    [Fact]
    public void CanonicalHashPreservesExactOperationAndIgnoresObjectPropertyOrderOnly()
    {
        var action = Action(); var hash = action.Fingerprint();
        Assert.Equal(hash, (action with { Arguments = Json("{\"nested\":{\"a\":1,\"b\":2},\"to\":\"owner\"}") }).Fingerprint());
        foreach (var changed in new[] { action with { ToolCallId = "other" }, action with { ToolId = "other" },
            action with { RunId = Guid.NewGuid() }, action with { ConversationId = Guid.NewGuid() }, action with { RunVersion = 8 },
            action with { RiskLevel = ToolRiskLevel.Destructive }, action with { Destination = "another recipient" },
            action with { Description = "A different displayed action" }, action with { Arguments = Json("{\"to\":\"attacker\"}") } })
            Assert.NotEqual(hash, changed.Fingerprint());
    }

    [Fact]
    public void InvalidUnknownUnboundedAndAmbiguousPayloadCannotBeApproved()
    {
        var action = Action();
        Assert.Throws<ArgumentException>(() => (action with { Arguments = Json("{\"to\":\"owner\",\"to\":\"attacker\"}") }).Fingerprint());
        foreach (var invalid in new[] { action with { RiskLevel = ToolRiskLevel.Unknown }, action with { Arguments = Json("[]") },
            action with { RunId = Guid.Empty }, action with { Destination = "" }, action with { ToolCallId = "" },
            action with { Source = new(ToolSourceKind.Mcp, "fixture"), ToolVersion = null } })
            Assert.Throws<ArgumentException>(() => invalid.Fingerprint());
        Assert.Throws<ArgumentException>(() => new ApprovalOwner("desktop", "").Validate());
    }

    [Fact]
    public void SnapshotOwnsJsonAfterOriginalDocumentDisposal()
    {
        var document = JsonDocument.Parse("{\"to\":\"owner\"}");
        var snapshot = (Action() with { Arguments = document.RootElement }).Snapshot(); document.Dispose();
        Assert.Equal("owner", snapshot.Arguments.GetProperty("to").GetString()); Assert.Equal(64, snapshot.Fingerprint().Length);
    }
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static PendingToolAction Action() => new(Guid.NewGuid(), Guid.NewGuid(), 7, "call-1", "send", new(ToolSourceKind.Local, "fixture"),
        ToolRiskLevel.Write, null, Json("{\"to\":\"owner\",\"nested\":{\"b\":2,\"a\":1}}"), "Send synthetic data", "owner");
}
