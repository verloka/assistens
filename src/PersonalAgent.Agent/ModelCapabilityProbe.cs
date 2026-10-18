using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Agent;

internal static class ModelCapabilityProbe
{
    // A synthetic 32x32 red PNG. Never capture a user's screen during Test Connection.
    private static readonly byte[] RedImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAvSURBVFhH7c6hAQAACMOw/f/08BwAJqKmKmnSz7LHdQAAAAAAAAAAAAAAAAAAAANUDfhqnpuFxwAAAABJRU5ErkJggg==");

    public static async Task<ModelCapabilities> VerifyAsync(IChatClient client, ModelProfile profile, CancellationToken ct)
    {
        if (!profile.Capabilities.SupportsToolCalling) return new();
        var nonce = Guid.NewGuid().ToString("N");
        await CheckAsync(new(ChatRole.User, "Call capability_probe with value exactly: " + nonce), nonce);
        if (profile.Capabilities.SupportsImages)
            await CheckAsync(new(ChatRole.User, [new TextContent("Identify the solid image color (red, green or blue). Call capability_probe with that lowercase color as value."),
                new DataContent(RedImage, "image/png")]), "red");
        return new(profile.Capabilities.SupportsImages, true);

        async Task CheckAsync(ChatMessage message, string expected)
        {
            // Declaration only: the provider can return arguments but cannot execute a function.
            var declaration = AIFunctionFactory.Create((string value) => value, "capability_probe").AsDeclarationOnly();
            var response = await client.GetResponseAsync([message], new()
            {
                ModelId = profile.ModelId, Tools = [declaration], ToolMode = ChatToolMode.RequireSpecific("capability_probe"), MaxOutputTokens = 128
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var calls = response.Messages.Where(item => item.Role == ChatRole.Assistant).SelectMany(item => item.Contents).OfType<FunctionCallContent>().ToArray();
            if (response.FinishReason != ChatFinishReason.ToolCalls || calls.Length != 1 || calls[0].Name != "capability_probe"
                || string.IsNullOrWhiteSpace(calls[0].CallId) || calls[0].Arguments is not { Count: 1 } arguments
                || !arguments.TryGetValue("value", out var value) || value?.ToString() != expected)
                throw new ModelProviderException("CapabilityProbeFailed", "The model did not confirm the configured function/image capability; the profile cannot be accepted for that use.");
        }
    }
}
