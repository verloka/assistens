namespace PersonalAgent.Core.Persistence;

// A durable receive position, scoped to a locally configured connection, never to message contents.
public sealed record ChannelCursor(long NextSequence = 0, long Version = 0);

public interface IChannelCursorRepository
{
    Task<ChannelCursor> ReadAsync(string channelId, string connectionId, CancellationToken ct = default);
    Task<ChannelCursor> AdvanceAsync(string channelId, string connectionId, ChannelCursor expected, long nextSequence, CancellationToken ct = default);
}

public sealed class ChannelCursorConflictException() : InvalidOperationException("Channel receive position changed; stop the receiver.");
