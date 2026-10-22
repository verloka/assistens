using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;
using PersonalAgent.Persistence.Entities;
using System.Text.Json;

namespace PersonalAgent.Persistence;

public sealed class SqliteApprovalRepository(IDbContextFactory<AgentDbContext> factory, TimeProvider? timeProvider = null) : IApprovalRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<ApprovalRequest> CreateAsync(PendingToolAction action, ApprovalOwner owner, TimeSpan lifetime, CancellationToken ct = default)
    {
        action = action.Snapshot(); owner.Validate();
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.TaskRuns.AnyAsync(run => run.Id == action.RunId && run.ConversationId == action.ConversationId
            && run.RunVersion == action.RunVersion && run.Status == RunStatus.WaitingForApproval, ct)) throw new ApprovalConflictException();
        var now = clock.GetUtcNow();
        var row = new ApprovalRequestRow { Id = Guid.NewGuid(), RunId = action.RunId, ToolCallId = action.ToolCallId, RunVersion = action.RunVersion,
            OwnerSource = owner.Source, OwnerId = owner.UserId, ActionJson = JsonSerializer.Serialize(action), ActionHash = action.Fingerprint(),
            CreatedAtUtc = now, ExpiresAtUtc = now + lifetime };
        db.ApprovalRequests.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { throw new ApprovalConflictException(); }
        return Read(row);
    }

    public async Task<ApprovalRequest?> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await ExpireAsync(db, ct);
        var row = await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == requestId, ct);
        return row is null || row.ActionJson.Length == 0 ? null : Read(row);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(ApprovalOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await factory.CreateDbContextAsync(ct); await ExpireAsync(db, ct);
        var rows = await db.ApprovalRequests.AsNoTracking().Where(row => row.OwnerSource == owner.Source && row.OwnerId == owner.UserId
            && row.Status == ApprovalStatus.Pending
            && db.TaskRuns.Any(run => run.Id == row.RunId && run.RunVersion == row.RunVersion && run.Status == RunStatus.WaitingForApproval))
            .OrderBy(row => row.CreatedAtUtc).ToArrayAsync(ct);
        return rows.Select(Read).ToArray();
    }

    public async Task<ApprovalRequest> DecideAsync(DecideApprovalCommand command, CancellationToken ct = default)
    {
        command.Owner.Validate();
        await using var db = await factory.CreateDbContextAsync(ct); await ExpireAsync(db, ct);
        var row = await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.RequestId, ct);
        if (row is null || row.ActionJson.Length == 0) throw new ApprovalConflictException();
        var request = Read(row); var now = clock.GetUtcNow();
        if (request.Owner != command.Owner || request.Version != command.ExpectedVersion || request.ActionHash != command.ActionHash
            || request.Status != ApprovalStatus.Pending || request.ExpiresAtUtc <= now) throw new ApprovalConflictException();
        var status = command.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        var updated = await db.ApprovalRequests.Where(candidate => candidate.Id == command.RequestId
            && candidate.Version == command.ExpectedVersion && candidate.Status == ApprovalStatus.Pending && candidate.ExpiresAtUtc > now
            && candidate.ActionHash == command.ActionHash && candidate.OwnerSource == command.Owner.Source && candidate.OwnerId == command.Owner.UserId
            && db.TaskRuns.Any(run => run.Id == candidate.RunId && run.ConversationId == request.Action.ConversationId
                && run.RunVersion == candidate.RunVersion && run.Status == RunStatus.WaitingForApproval))
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.Status, status).SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.DecidedAtUtc, now), ct);
        if (updated != 1) throw new ApprovalConflictException();
        return request with { Status = status, Version = request.Version + 1, DecidedAtUtc = now, DecisionBy = command.Owner };
    }

    private Task<int> ExpireAsync(AgentDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.ApprovalRequests.Where(row => row.PayloadSchemaVersion == 1 && row.Status == ApprovalStatus.Pending && row.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, ApprovalStatus.Expired).SetProperty(row => row.Version, row => row.Version + 1)
                .SetProperty(row => row.DecidedAtUtc, now), ct);
    }

    internal static ApprovalRequest Read(ApprovalRequestRow row)
    {
        if (row.PayloadSchemaVersion != 1) throw new NotSupportedException("Unsupported approval payload version.");
        var action = JsonSerializer.Deserialize<PendingToolAction>(row.ActionJson)?.Snapshot() ?? throw new InvalidDataException("Invalid pending action.");
        var owner = new ApprovalOwner(row.OwnerSource, row.OwnerId); owner.Validate();
        if (action.RunId != row.RunId || action.ToolCallId != row.ToolCallId || action.RunVersion != row.RunVersion
            || action.Fingerprint() != row.ActionHash || !Enum.IsDefined(row.Status) || row.Version < 0 || row.ExpiresAtUtc <= row.CreatedAtUtc)
            throw new InvalidDataException("Approval identity or action integrity check failed.");
        return new(row.Id, action, row.ActionHash, owner, row.Status, row.Version, row.CreatedAtUtc, row.ExpiresAtUtc,
            row.DecidedAtUtc, row.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected ? owner : null);
    }
}
