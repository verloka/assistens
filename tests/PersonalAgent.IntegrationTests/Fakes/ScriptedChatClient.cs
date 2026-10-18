using Microsoft.Extensions.AI;

namespace PersonalAgent.IntegrationTests.Fakes;

// Executes only in-process test callbacks; no HTTP, credentials or provider SDK.
internal sealed class ScriptedChatClient(
    params Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>[] steps) : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> _steps = new(steps);
    public int CallCount { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        if (_steps.Count == 0)
            throw new InvalidOperationException("Unexpected model call: the deterministic script is exhausted.");
        return Task.FromResult(_steps.Dequeue()(messages.ToArray(), options));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This diagnostic fake covers non-streaming calls only.");

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted-local-test")
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
