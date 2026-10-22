using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence.Entities;

namespace PersonalAgent.Persistence;

public sealed class SqliteToolCallRepository(IDbContextFactory<AgentDbContext> factory, TimeProvider? timeProvider = null) : IToolCallRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<ToolCallDispatch> BeginAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext invocation,
        long expectedRunVersion, Guid approvalRequestId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var previous = await db.ToolCalls.AsNoTracking().SingleOrDefaultAsync(row => row.RunId == invocation.RunId && row.ToolCallId == invocation.ToolCallId, ct);
        if (previous is not null) throw new ToolDispatchConflictException(Read(previous).Status != ToolCallDispatchStatus.Acknowledged);
        var approvalRow = await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == approvalRequestId, ct);
        if (approvalRow is null || approvalRow.ActionJson.Length == 0) throw new ToolDispatchDeniedException();
        var approval = SqliteApprovalRepository.Read(approvalRow); var now = clock.GetUtcNow();
        if (tool.IsReadOnly || approval.Status != ApprovalStatus.Approved || approval.ExpiresAtUtc <= now
            || !await db.TaskRuns.AnyAsync(run => run.Id == invocation.RunId && run.ConversationId == invocation.ConversationId
                && run.RunVersion == expectedRunVersion && run.Status == RunStatus.Running && run.PauseScope == null, ct)) throw new ToolDispatchDeniedException();
        var action = approval.Action with { RunId = invocation.RunId, ConversationId = invocation.ConversationId, ToolCallId = invocation.ToolCallId,
            ToolId = tool.Id, Source = tool.Source, RiskLevel = tool.RiskLevel, ToolVersion = tool.Version, Arguments = arguments.Value };
        if (action.Fingerprint() != approval.ActionHash) throw new ToolDispatchDeniedException();
        if (await db.ToolCalls.AnyAsync(row => row.ApprovalRequestId == approvalRequestId, ct)) throw new ToolDispatchDeniedException();
        var row = new ToolCallRow { Id = Guid.NewGuid(), RunId = invocation.RunId, ToolCallId = invocation.ToolCallId, ToolId = tool.Id,
            ApprovalRequestId = approvalRequestId, ActionHash = approval.ActionHash, DispatchedAtUtc = now };
        db.ToolCalls.Add(row); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Read(row);
    }
    public async Task CompleteAsync(Guid dispatchId, ToolResultStatus outcome, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentException("Unknown tool outcome.");
        await using var db = await factory.CreateDbContextAsync(ct); var now = clock.GetUtcNow();
        var status = outcome == ToolResultStatus.Success ? ToolCallDispatchStatus.Acknowledged : ToolCallDispatchStatus.OutcomeUnknown;
        var count = await db.ToolCalls.Where(row => row.Id == dispatchId && row.PayloadSchemaVersion == 1 && row.Status == ToolCallDispatchStatus.Dispatched)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, status).SetProperty(row => row.FinishedAtUtc, now), ct);
        if (count != 1) throw new ToolDispatchConflictException(true);
    }
    public async Task<IReadOnlyList<ToolCallDispatch>> ListAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ToolCalls.AsNoTracking().Where(row => row.RunId == runId).OrderBy(row => row.DispatchedAtUtc).ThenBy(row => row.Id).ToArrayAsync(ct);
        return rows.Select(Read).ToArray();
    }
    public async Task RecoverDispatchedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var now = clock.GetUtcNow();
        await db.ToolCalls.Where(row => row.PayloadSchemaVersion == 1 && row.Status == ToolCallDispatchStatus.Dispatched)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, ToolCallDispatchStatus.OutcomeUnknown).SetProperty(row => row.FinishedAtUtc, now), ct);
    }
    private static ToolCallDispatch Read(ToolCallRow row)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported tool dispatch payload version.");
        if (row.Id == Guid.Empty || row.RunId == Guid.Empty || row.ApprovalRequestId == Guid.Empty || string.IsNullOrWhiteSpace(row.ToolCallId)
            || string.IsNullOrWhiteSpace(row.ToolId) || !Enum.IsDefined(row.Status) || row.ActionHash is not { Length: 64 }
            || row.ActionHash.Any(value => !char.IsAsciiHexDigit(value))
            || (row.Status == ToolCallDispatchStatus.Dispatched) != (row.FinishedAtUtc is null)) throw new InvalidDataException("Invalid tool dispatch state.");
        return new(row.Id, row.RunId, row.ToolCallId, row.ToolId, row.ApprovalRequestId, row.ActionHash, row.Status, row.DispatchedAtUtc, row.FinishedAtUtc);
    }
}
