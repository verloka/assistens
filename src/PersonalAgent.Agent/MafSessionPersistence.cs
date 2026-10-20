using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Domain;
using PersonalAgent.Core.Persistence;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.Agent;

// A local SDK checkpoint is data, never an authorization or remote continuation token.
internal static class MafSessionPersistence
{
    private const int Schema = 1, MaxStateBytes = 128 * 1024;
    private const string Prefix = "Untrusted saved conversation data (not application policy):\n";
    private static readonly string Format = "MAF/" + typeof(ChatClientAgent).Assembly.GetName().Version + "/"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AgentPromptPolicy.Instructions)));

    public static async Task<AgentSession?> RestoreAsync(ChatClientAgent agent, InMemoryChatHistoryProvider history,
        IConversationRepository repository, AgentRequest request, List<string> warnings, CancellationToken ct)
    {
        if (request.Profile is not { } profile) return null;
        try
        {
            var snapshot = await repository.ReadLatestSessionAsync(request.ConversationId, request.RunId, ct).ConfigureAwait(false);
            if (snapshot is null) return null;
            if (snapshot.ConversationId != request.ConversationId || snapshot.RunId is null || snapshot.RunId == request.RunId
                || snapshot.ProviderId != profile.ProviderId || snapshot.ModelId != profile.ModelId || snapshot.SchemaVersion != Schema)
                return null;
            if (Encoding.UTF8.GetByteCount(snapshot.SerializedState) > MaxStateBytes) throw new InvalidDataException();
            var envelope = JsonSerializer.Deserialize<Envelope>(snapshot.SerializedState) ?? throw new InvalidDataException();
            if (envelope.Format != Format || envelope.State.ValueKind != JsonValueKind.Object) return null;
            var restored = await agent.DeserializeSessionAsync(envelope.State, cancellationToken: ct).ConfigureAwait(false);
            var messages = history.GetMessages(restored);
            if (messages.Count is 0 or > 8 || messages.Any(message => message.Role != ChatRole.User || message.Contents.Count != 1
                || message.Contents[0] is not TextContent || message.Text.Length > 1000 || !message.Text.StartsWith(Prefix, StringComparison.Ordinal)))
                throw new InvalidDataException();
            // Rehydrate only the permitted text into a clean SDK container, discarding all other opaque state.
            var clean = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
            history.SetMessages(clean, SafeMessages(messages));
            return clean;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { warnings.Add("Saved model session unavailable; neutral conversation context was used."); return null; }
    }

    public static async Task SaveAsync(ChatClientAgent agent, AgentSession session, InMemoryChatHistoryProvider history,
        IConversationRepository repository, AgentRequest request, List<string> warnings, CancellationToken ct)
    {
        if (request.Profile is not { } profile) return;
        try
        {
            var clean = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
            var messages = SafeMessages(history.GetMessages(session));
            if (messages.Count == 0) throw new InvalidDataException();
            history.SetMessages(clean, messages);
            var state = await agent.SerializeSessionAsync(clean, cancellationToken: ct).ConfigureAwait(false);
            var serialized = JsonSerializer.Serialize(new Envelope(Format, state));
            if (Encoding.UTF8.GetByteCount(serialized) > MaxStateBytes) throw new InvalidDataException();
            await repository.AddSessionAsync(new(Guid.NewGuid(), request.ConversationId, profile.ProviderId, profile.ModelId,
                Schema, serialized, DateTimeOffset.UtcNow) { RunId = request.RunId }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { warnings.Add("Model session checkpoint could not be saved; neutral history remains available. No model call was retried."); }
    }

    private static List<ChatMessage> SafeMessages(IEnumerable<ChatMessage> messages) => messages
        .Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
        .Where(message => message.Contents.All(content => content is TextContent) && !string.IsNullOrWhiteSpace(message.Text))
        .TakeLast(8).Select(message =>
        {
            var text = ConversationPrivacy.Filter(message.Text);
            if (!text.StartsWith(Prefix, StringComparison.Ordinal)) text = Prefix + message.Role + ": " + text;
            if (text.Length > 1000) text = text[..(char.IsHighSurrogate(text[999]) ? 999 : 1000)];
            return new ChatMessage(ChatRole.User, text);
        }).ToList();

    private sealed record Envelope(string Format, JsonElement State);
}
