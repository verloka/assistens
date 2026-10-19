using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpRunPermissionTests
{
    [Fact]
    public async Task ActualSdkRequiresBothScopesAndNewVersionOrWriteCannotInheritReadPermission()
    {
        var calls = 0; var description = "Synthetic read";
        await using var server = McpTestServer.Program.CreateHttpServer(listTools: () =>
            [new Tool { Name = "read", Description = description, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) },
             new Tool { Name = "new_write", Description = "New tool claiming to be safe", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                 Annotations = new() { ReadOnlyHint = true } }], callTool: (_, _) =>
            { Interlocked.Increment(ref calls); return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Synthetic read result" }] }); });
        await server.StartAsync();
        var endpoint = new Uri(Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp");
        await using var connection = await new McpConnectionFactory([new HttpMcpTransportProvider(new Secrets())]).ConnectAsync(new()
        { Id = Guid.NewGuid(), Name = "Fixture", Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
            Http = new(endpoint, AllowLoopbackHttp: true), AllowedTools = ["read", "new_write"] });
        var discovery = new McpToolDiscovery(); var catalog = await discovery.RefreshAsync(connection);
        var declaration = Assert.Single(catalog.Tools, value => value.ServerName == "read");
        var unknown = Assert.Single(catalog.Tools, value => value.ServerName == "new_write");
        var grant = new McpToolPermission(connection.Configuration.Id, declaration.Descriptor.Id, declaration.Version, ToolRiskLevel.ReadOnly);
        var read = new McpAgentTool(connection, declaration, grant);
        var request = new AgentRequest { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "Synthetic read",
            ModelProfileId = "fixture", AllowedToolIds = [read.Descriptor.Id, unknown.Descriptor.Id] };
        var access = new ToolAccessContext(request.RunId, request.ConversationId, [ToolRiskLevel.ReadOnly, ToolRiskLevel.Write]);
        var rule = new RunToolPermission(read.Descriptor.Id, read.Descriptor.Source, ToolRiskLevel.ReadOnly, ToolPermissionEffect.Allow, declaration.Version);
        var policy = new ToolPermissionPolicy(access, [rule], [grant]);
        ToolRegistry Registry(params IAgentTool[] tools) => new(tools, (_, _, _) => Task.CompletedTask,
            (tool, context, _) => ValueTask.FromResult(policy.Evaluate(tool, context) == ToolPermissionDecision.Allowed), access);
        var binding = Registry(read, unknown).Bind(request); var args = JsonSerializer.SerializeToElement(new { });
        Assert.Equal(ToolResultStatus.Success, (await binding.InvokeAsync(read.Descriptor.Id, "read-1", args, default)).Result.Status);
        Assert.Equal("ToolPermissionDenied", (await binding.InvokeAsync(unknown.Descriptor.Id, "new-write", args, default)).Result.ErrorCode);
        Assert.Equal(1, calls);
        policy = new(access, [rule], []);
        Assert.Equal("ToolPermissionDenied", (await binding.InvokeAsync(read.Descriptor.Id, "revoked", args, default)).Result.ErrorCode); Assert.Equal(1, calls);
        description = "Updated reviewed metadata";
        declaration = Assert.Single((await discovery.RefreshAsync(connection)).Tools, value => value.ServerName == "read");
        var updatedGrant = grant with { Version = declaration.Version };
        var updated = new McpAgentTool(connection, declaration, updatedGrant);
        policy = new(access, [rule], [updatedGrant]);
        var updatedRequest = request with { AllowedToolIds = [updated.Descriptor.Id] };
        Assert.Equal("ToolPermissionDenied", (await Registry(updated).Bind(updatedRequest).InvokeAsync(updated.Descriptor.Id, "changed-version", args, default)).Result.ErrorCode);
        var updatedRule = rule with { Version = declaration.Version };
        policy = new(access, [updatedRule], [updatedGrant]);
        Assert.Equal(ToolResultStatus.Success, (await Registry(updated).Bind(updatedRequest).InvokeAsync(updated.Descriptor.Id, "reviewed-again", args, default)).Result.Status);
        var writeGrant = updatedGrant with { RiskLevel = ToolRiskLevel.Write };
        var write = new McpAgentTool(connection, declaration, writeGrant);
        policy = new(access, [updatedRule with { RiskLevel = ToolRiskLevel.Write }], [writeGrant]);
        Assert.Equal("ToolPermissionDenied", (await Registry(write).Bind(updatedRequest).InvokeAsync(write.Descriptor.Id, "write-needs-approval", args, default)).Result.ErrorCode);
        Assert.Equal(2, calls);
        await server.StopAsync();
    }

    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
