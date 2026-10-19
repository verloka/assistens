using PersonalAgent.Core.Agents;

namespace PersonalAgent.Core.Security;

public interface IToolInvocationAuthorizer
{
    // Arguments already passed the registered schema. This gate may only restrict existing tool authority.
    ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, CancellationToken ct);
    // Rechecked after any boundary/policy wait, immediately before dispatch. Approval receipts are single use.
    ValueTask<ToolDispatchAuthorization> ValidateDispatchAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation, CancellationToken ct);
}

// Issued by trusted invocation authorization; the durable dispatcher independently validates its approval.
public sealed record ToolDispatchAuthorization(bool Allowed, Guid? ApprovalRequestId = null, long? ExpectedRunVersion = null);
