using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

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

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        { cancellationToken.ThrowIfCancellationRequested(); yield return update; }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted-local-test")
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
