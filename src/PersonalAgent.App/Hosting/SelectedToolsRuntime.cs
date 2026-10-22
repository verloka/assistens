using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;

namespace PersonalAgent.App;

// Ordinary application composition, using the same registry/approval/lease/ledger as the contract suites.
public sealed class SelectedToolsRuntime(TaskRunCoordinator runs, IDesktopAccessCoordinator desktop, ApprovalOwner owner,
    McpConnectionManager manager, IApprovalRepository? approvals = null, IToolCallRepository? dispatches = null,
    IMcpConnectionRepository? connections = null, IConversationRepository? conversations = null,
    Func<DesktopLease, IObservedComputerController>? createController = null)
{
    public async Task<AgentRunResult> ExecuteAsync(IModelProvider provider, ModelProfile profile, AgentRequest request,
        IProgress<AgentEvent> progress, CancellationToken ct)
    {
        if (approvals is null || dispatches is null || runs.GetRun(request.RunId).Origin.Source != "desktop") return Blocked("LocalToolSelectionRequired");
        await using var computer = new ComputerToolSession(request.RunId, desktop, createController);
        try
        {
            var rules = ToolPermissionPolicy.SnapshotSelection(request.ToolPermissions ?? []);
            if (!rules.Select(rule => rule.ToolId).Order(StringComparer.Ordinal).SequenceEqual(request.AllowedToolIds.Order(StringComparer.Ordinal))) return Blocked("ToolSelectionMismatch");
            var tools = new List<IAgentTool>(); var grants = new List<McpToolPermission>();
            var mcp = new Dictionary<string, McpAgentTool>(StringComparer.Ordinal);
            var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
            var saved = connections is null ? [] : await connections.ListAsync(ct);
            foreach (var rule in rules)
            {
                IAgentTool tool;
                if (rule.Source.Kind == ToolSourceKind.Computer)
                    tool = computer.Tools.SingleOrDefault(value => value.Descriptor.Id == rule.ToolId) ?? throw new ArgumentException();
                else
                {
                    var connectionId = Guid.ParseExact(rule.Source.Id, "N");
                    var configuration = saved.SingleOrDefault(value => value.Configuration.Id == connectionId) ?? throw new ArgumentException();
                    var grant = configuration.Permissions.SingleOrDefault(value => value.ToolId == rule.ToolId && value.Version == rule.Version && value.RiskLevel == rule.RiskLevel)
                        ?? throw new ArgumentException();
                    var bound = manager.BindTool(connectionId, grant);
                    if (!bound.IsCurrent(configuration) || bound.BindingId != rule.BindingId) return Blocked("ToolSelectionStale");
                    mcp.Add(rule.ToolId, bound); grants.Add(grant); tool = bound;
                    destinations.Add(rule.ToolId, configuration.Configuration.Name + " [" + connectionId.ToString("N") + "]"
                        + (configuration.Configuration.Http is { } http ? " " + http.Endpoint.GetLeftPart(UriPartial.Authority) : " local stdio destination"));
                }
                if (tool.Descriptor.Source != rule.Source || tool.Descriptor.RiskLevel != rule.RiskLevel || tool.Descriptor.Version != rule.Version) return Blocked("ToolSelectionStale");
                tools.Add(tool);
            }
            var access = new ToolAccessContext(request.RunId, request.ConversationId, rules.Select(rule => rule.RiskLevel));
            var policy = new ToolPermissionPolicy(access, rules, grants);
            async ValueTask<bool> Current(ToolDescriptor tool, ToolExecutionContext context, CancellationToken token)
            {
                if (policy.EvaluateScope(tool, context) == ToolPermissionDecision.Denied) return false;
                if (tool.Source.Kind != ToolSourceKind.Mcp) return true;
                var profiles = await connections!.ListAsync(token);
                var config = profiles.SingleOrDefault(value => value.Configuration.Id.ToString("N") == tool.Source.Id);
                return config is not null && mcp[tool.Id].IsCurrent(config);
            }
            var human = new HumanApprovalAuthorizer(runs, approvals, policy.EvaluateScope, owner,
                (tool, _) => ("Review the exact " + tool.Id + " action and all arguments.",
                    tool.Source.Kind == ToolSourceKind.Computer ? "Current Windows desktop; changed screen cancels input" : destinations[tool.Id]), desktop);
            var registry = new ToolRegistry(tools, runs.WaitAtBoundaryAsync, Current, access,
                new InputApproval(human, computer, policy), dispatches, policy);
            return await new MafAgentRuntime(provider, profile, registry, runs.WaitAtBoundaryAsync, computer.ConfirmModelObservation, conversations)
                .ExecuteAsync(request, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RunPersistenceException) { throw; }
        catch (Exception) { return Blocked("ToolSelectionUnavailable"); }
    }

    private static AgentRunResult Blocked(string code) => new() { Status = AgentResultStatus.Blocked, ErrorCode = code,
        Summary = "The selected tools are unavailable, changed or not authorized. Review a new per-task selection; no automatic retry was performed." };

    private sealed class InputApproval(HumanApprovalAuthorizer human, ComputerToolSession computer, ToolPermissionPolicy policy) : IToolInvocationAuthorizer
    {
        public async ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, CancellationToken ct)
        {
            if (policy.EvaluateScope(tool, invocation) == ToolPermissionDecision.RequiresApproval)
            {
                if (tool.Source.Kind == ToolSourceKind.Computer && !tool.IsReadOnly)
                { if (!await computer.PrepareInputApprovalAsync(tool, arguments, invocation, ct)) return false; }
                else await computer.YieldDesktopAsync(ct);
            }
            return await human.AuthorizeAsync(tool, arguments, invocation, ct);
        }

        public async ValueTask<ToolDispatchAuthorization> ValidateDispatchAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, CancellationToken ct)
        {
            var authorization = await human.ValidateDispatchAsync(tool, arguments, invocation, ct);
            if (authorization.Allowed && tool.Source.Kind == ToolSourceKind.Computer && !tool.IsReadOnly
                && !await computer.RevalidateInputApprovalAsync(tool, arguments, invocation, ct)) return new(false);
            return authorization;
        }
    }
}
