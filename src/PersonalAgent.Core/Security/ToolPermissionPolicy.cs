using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Security;

public enum ToolPermissionEffect { Deny, Allow }
public enum ToolPermissionDecision { Denied, Allowed, RequiresApproval }
public sealed record RunToolPermission(string ToolId, ToolSource Source, ToolRiskLevel RiskLevel,
    ToolPermissionEffect Effect, string? Version = null);

// Trusted composition snapshots both scopes. No model argument or server hint creates a grant.
public sealed class ToolPermissionPolicy
{
    private readonly ToolAccessContext access;
    private readonly RunToolPermission[] rules;
    private readonly McpToolPermission[] connections;

    public ToolPermissionPolicy(ToolAccessContext access, IEnumerable<RunToolPermission> runRules,
        IEnumerable<McpToolPermission>? connectionPermissions = null)
    {
        ArgumentNullException.ThrowIfNull(access); ArgumentNullException.ThrowIfNull(runRules);
        this.access = access; rules = runRules.ToArray(); connections = connectionPermissions?.ToArray() ?? [];
        if (rules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.ToolId) || rule.Source is null
                || string.IsNullOrWhiteSpace(rule.Source.Id) || !Enum.IsDefined(rule.Source.Kind)
                || !Enum.IsDefined(rule.RiskLevel) || !Enum.IsDefined(rule.Effect))
            || connections.Any(rule => rule is null || rule.ConnectionId == Guid.Empty || string.IsNullOrWhiteSpace(rule.ToolId)
                || string.IsNullOrWhiteSpace(rule.Version) || !Enum.IsDefined(rule.RiskLevel)))
            throw new ArgumentException("Tool permission rules must have explicit valid identities and classifications.");
    }

    public ToolPermissionDecision Evaluate(ToolDescriptor descriptor, ToolExecutionContext invocation)
    {
        if (!access.Allows(descriptor, invocation)) return ToolPermissionDecision.Denied;
        // Deny covers the exact tool/source regardless of a competing classification/version allow.
        var matching = rules.Where(rule => rule.ToolId == descriptor.Id && rule.Source == descriptor.Source).ToArray();
        if (matching.Any(rule => rule.Effect == ToolPermissionEffect.Deny)
            || !matching.Any(rule => rule.Effect == ToolPermissionEffect.Allow && rule.RiskLevel == descriptor.RiskLevel
                && rule.Version == descriptor.Version)) return ToolPermissionDecision.Denied;
        if (descriptor.Source.Kind == ToolSourceKind.Mcp)
        {
            if (!Guid.TryParseExact(descriptor.Source.Id, "N", out var connectionId) || string.IsNullOrWhiteSpace(descriptor.Version))
                return ToolPermissionDecision.Denied;
            var grants = connections.Where(rule => rule.ConnectionId == connectionId && rule.ToolId == descriptor.Id).ToArray();
            if (grants.Length != 1 || grants[0].Version != descriptor.Version || grants[0].RiskLevel != descriptor.RiskLevel)
                return ToolPermissionDecision.Denied;
        }
        // This is a scope check, never an approval token. Durable exact-action consent follows separately.
        return descriptor.RiskLevel == ToolRiskLevel.ReadOnly ? ToolPermissionDecision.Allowed : ToolPermissionDecision.RequiresApproval;
    }
}
