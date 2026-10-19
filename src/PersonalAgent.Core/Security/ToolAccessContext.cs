using System.Collections.Frozen;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Security;

// Supplied by trusted composition, never deserialized from model/tool arguments.
// This ceiling restricts authority; it does not grant tool, destination or action approval.
public sealed class ToolAccessContext
{
    private readonly FrozenSet<ToolRiskLevel> categories;
    public Guid RunId { get; }
    public Guid ConversationId { get; }

    public ToolAccessContext(Guid runId, Guid conversationId, IEnumerable<ToolRiskLevel> allowedCategories)
    {
        if (runId == Guid.Empty || conversationId == Guid.Empty) throw new ArgumentException("An access context requires run and conversation identities.");
        ArgumentNullException.ThrowIfNull(allowedCategories);
        categories = allowedCategories.ToFrozenSet();
        if (categories.Any(category => category == ToolRiskLevel.Unknown || !Enum.IsDefined(category)))
            throw new ArgumentException("Only explicitly known action categories can be permitted.", nameof(allowedCategories));
        RunId = runId; ConversationId = conversationId;
    }

    public bool Allows(ToolDescriptor descriptor, ToolExecutionContext invocation) =>
        invocation.RunId == RunId && invocation.ConversationId == ConversationId
        && !string.IsNullOrWhiteSpace(invocation.ToolCallId)
        && descriptor.RiskLevel != ToolRiskLevel.Unknown && categories.Contains(descriptor.RiskLevel)
        && (descriptor.RiskLevel != ToolRiskLevel.ReadOnly || descriptor.IsReadOnly);
}
