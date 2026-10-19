using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Security;

public enum ToolPermissionEffect { Deny, Allow }
public enum ToolPermissionDecision { Denied, Allowed, RequiresApproval }
public sealed record ToolDestinationConstraint(string JsonPointer, IReadOnlyList<string> AllowedValues);
public sealed record RunToolPermission(string ToolId, ToolSource Source, ToolRiskLevel RiskLevel,
    ToolPermissionEffect Effect, string? Version = null, IReadOnlyList<ToolDestinationConstraint>? Destinations = null);

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
        rules = rules.Select(rule => rule with { Destinations = rule.Destinations?.Select(Snapshot).ToArray() }).ToArray();
    }

    // Scope-only preflight cannot authorize a target. Registry always uses the argument-aware overload.
    public ToolPermissionDecision EvaluateScope(ToolDescriptor descriptor, ToolExecutionContext invocation) => EvaluateCore(descriptor, invocation, null, false);
    public ToolPermissionDecision Evaluate(ToolDescriptor descriptor, ToolExecutionContext invocation) => EvaluateCore(descriptor, invocation, null, true);
    public ToolPermissionDecision Evaluate(ToolDescriptor descriptor, ToolArguments arguments, ToolExecutionContext invocation) => EvaluateCore(descriptor, invocation, arguments, true);

    private ToolPermissionDecision EvaluateCore(ToolDescriptor descriptor, ToolExecutionContext invocation, ToolArguments? arguments, bool checkTargets)
    {
        if (!access.Allows(descriptor, invocation)) return ToolPermissionDecision.Denied;
        // Deny covers the exact tool/source regardless of a competing classification/version allow.
        var matching = rules.Where(rule => rule.ToolId == descriptor.Id && rule.Source == descriptor.Source).ToArray();
        if (matching.Any(rule => rule.Effect == ToolPermissionEffect.Deny)
            || !matching.Any(rule => rule.Effect == ToolPermissionEffect.Allow && rule.RiskLevel == descriptor.RiskLevel
                && rule.Version == descriptor.Version && (!checkTargets || TargetsMatch(rule.Destinations, arguments)))) return ToolPermissionDecision.Denied;
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

    private static ToolDestinationConstraint Snapshot(ToolDestinationConstraint constraint)
    {
        if (constraint is null || constraint.JsonPointer is not { Length: > 1 and <= 1024 } pointer || pointer[0] != '/'
            || pointer[1..].Split('/').Length > 16 || pointer[1..].Split('/').Any(part => part.Length == 0)
            || constraint.AllowedValues is null || constraint.AllowedValues.Count > 100
            || constraint.AllowedValues.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)))
            throw new ArgumentException("Destination rules require bounded exact object paths and literal allowed values.");
        for (var index = 0; index < pointer.Length; index++)
            if (pointer[index] == '~' && (++index >= pointer.Length || pointer[index] is not ('0' or '1')))
                throw new ArgumentException("Invalid destination JSON pointer escape.");
        return constraint with { AllowedValues = Array.AsReadOnly(constraint.AllowedValues.Distinct(StringComparer.Ordinal).ToArray()) };
    }
    private static bool TargetsMatch(IReadOnlyList<ToolDestinationConstraint>? constraints, ToolArguments? arguments)
    {
        if (constraints is not { Count: > 0 }) return true;
        if (arguments is null) return false;
        foreach (var constraint in constraints)
        {
            var value = arguments.Value;
            foreach (var part in constraint.JsonPointer[1..].Split('/'))
            {
                var name = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (value.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                var matches = value.EnumerateObject().Where(property => property.Name == name).ToArray();
                if (matches.Length != 1) return false;
                value = matches[0].Value;
            }
            if (value.ValueKind != System.Text.Json.JsonValueKind.String || !constraint.AllowedValues.Contains(value.GetString()!, StringComparer.Ordinal)) return false;
        }
        return true;
    }
}
