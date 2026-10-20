namespace PersonalAgent.Core.Channels;

// A confirmed rejection may be retried by a durable outbox; an unknown write outcome never may.
public sealed class ChannelDeliveryException(string code, bool outcomeUnknown, bool retryable = false, int acceptedParts = 0, TimeSpan? retryAfter = null)
    : Exception("Channel delivery stopped: " + code)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public bool Retryable { get; } = retryable && !outcomeUnknown && acceptedParts == 0;
    public int AcceptedParts { get; } = acceptedParts;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
