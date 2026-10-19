using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Persistence;

public interface IApprovalRepository
{
    Task<ApprovalRequest> CreateAsync(PendingToolAction action, ApprovalOwner owner, TimeSpan lifetime, CancellationToken ct = default);
    Task<ApprovalRequest?> GetAsync(Guid requestId, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(ApprovalOwner owner, CancellationToken ct = default);
    Task<ApprovalRequest> DecideAsync(DecideApprovalCommand command, CancellationToken ct = default);
}
