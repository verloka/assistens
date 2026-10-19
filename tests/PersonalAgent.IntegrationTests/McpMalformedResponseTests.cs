using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Mcp;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpMalformedResponseTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("content-type")]
    [InlineData("image-data")]
    public async Task DamagedActualToolReplyStopsModelAndDoesNotReplayAnUnconfirmedWrite(string damage)
    {
        await using var server = McpTestServer.Program.CreateHttpServer(); await server.StartAsync();
        try
        {
            CorruptReply? handler = null;
            var provider = new HttpMcpTransportProvider(new Secrets(), () => handler = new(damage));
            var endpoint = new Uri(Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp");
            await using var connection = await new McpConnectionFactory([provider]).ConnectAsync(new()
            {
                Id = Guid.NewGuid(), Name = "Malformed local reply", Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
                Http = new(endpoint, AllowLoopbackHttp: true), AllowedTools = ["echo"], RequestTimeout = TimeSpan.FromSeconds(2)
            });
            var discovered = Assert.Single((await new McpToolDiscovery().RefreshAsync(connection)).Tools);
            // Treat even this harmless local echo as a reviewed write to verify conservative unknown outcomes.
            var tool = new McpAgentTool(connection, discovered, new(connection.Configuration.Id, discovered.Descriptor.Id, discovered.Version, ToolRiskLevel.Write));
            var registry = new ToolRegistry([tool], (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
            using var client = new ScriptedChatClient((_, _) => new(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("one-call", tool.Descriptor.Id, new Dictionary<string, object?> { ["message"] = "local diagnostic" })]))
            { FinishReason = ChatFinishReason.ToolCalls });
            var runtime = new MafAgentRuntime((_, _) => ValueTask.FromResult<IChatClient>(client), (_, _) => Task.CompletedTask, registry);
            var result = await runtime.ExecuteAsync(new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "fake",
                Instructions = "Synthetic protocol corruption", AllowedToolIds = [tool.Descriptor.Id] }, new Progress(), CancellationToken.None);
            Assert.Equal(AgentResultStatus.Blocked, result.Status);
            Assert.Contains(result.ErrorCode, new[] { "McpRequestFailed", "McpRequestTimedOut", "McpToolOutcomeUnknown" });
            Assert.Equal(1, handler!.ToolCalls); Assert.Equal(1, client.CallCount);
            Assert.DoesNotContain("private-malformed-body", JsonSerializer.Serialize(result));
        }
        finally { await server.StopAsync(); }
    }

    private sealed class CorruptReply(string damage) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        public int ToolCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            JsonElement? id = null;
            if (request.Content is not null)
            {
                using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                if (payload.RootElement.GetProperty("method").GetString() == "tools/call") id = payload.RootElement.GetProperty("id").Clone();
            }
            var response = await base.SendAsync(request, ct);
            if (id is null) return response;
            // The actual server processed the call; corrupt only the response envelope before the pinned SDK reads it.
            Interlocked.Increment(ref ToolCalls); response.Content.Dispose();
            var content = damage switch
            {
                "json" => "{private-malformed-body",
                "content-type" => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "unsupported-private-malformed-body" } } } }),
                _ => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "image", mimeType = "image/png", data = "%%%private-malformed-body%%%" } } } })
            };
            response.Content = new StringContent(content, Encoding.UTF8, "application/json");
            response.Headers.TransferEncodingChunked = false;
            return response;
        }
    }
    private sealed class Progress : IProgress<AgentEvent> { public void Report(AgentEvent value) { } }
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
