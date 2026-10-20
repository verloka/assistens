using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Security;

namespace PersonalAgent.Core.Application;

// Transport parsers/callback handlers use the same commands as local UI, after fresh peer authentication.
// This is not a model tool and does not turn action tokens/text into approval decisions.
public sealed class ChannelOwnerCommands(ChannelOwnershipPolicy ownership, ITaskCommands commands, IApprovalRepository approvals)
{
    public async Task<TaskRun> CancelAsync(IncomingMessage peer, CancelRunCommand command, CancellationToken ct = default)
    {
        await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        return await commands.CancelAsync(command, ct).ConfigureAwait(false);
    }
    public async Task<TaskRun> PauseAsync(IncomingMessage peer, PauseRunCommand command, CancellationToken ct = default)
    {
        await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        return await commands.PauseAsync(command, ct).ConfigureAwait(false);
    }
    public async Task<TaskRun> ResumeAsync(IncomingMessage peer, ResumeRunCommand command, CancellationToken ct = default)
    {
        await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        return await commands.ResumeAsync(command, ct).ConfigureAwait(false);
    }
    public async Task<ApprovalRequest> DecideAsync(IncomingMessage peer, DecideApprovalCommand command, CancellationToken ct = default)
    {
        var actor = await ownership.RequireOwnerAsync(peer, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        return await approvals.DecideAsync(command with { Owner = actor }, ct).ConfigureAwait(false);
    }
}
