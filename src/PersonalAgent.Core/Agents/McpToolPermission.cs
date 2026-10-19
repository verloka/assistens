namespace PersonalAgent.Core.Agents;

// Trusted owner classification, pinned to the reviewed declaration. Server hints cannot create this record.
public sealed record McpToolPermission(Guid ConnectionId, string ToolId, string Version, ToolRiskLevel RiskLevel,
    bool IndependentOfDesktop = false);
