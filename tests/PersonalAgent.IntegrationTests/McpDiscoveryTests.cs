using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using PersonalAgent.Mcp;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class McpDiscoveryTests
{
    [Fact]
    public async Task ActualDiscoveryUsesStableConnectionNamespaceAndRegistryValidatesSchemaWithoutGrantingRemoteAnnotations()
    {
        var calls = 0;
        await using var server = McpTestServer.Program.CreateHttpServer(async context =>
        {
            if (context.Request.Method != "POST") return;
            context.Request.EnableBuffering(); using var body = await JsonDocument.ParseAsync(context.Request.Body);
            context.Request.Body.Position = 0;
            if (body.RootElement.GetProperty("method").GetString() == "tools/call") Interlocked.Increment(ref calls);
        }, () => [Declaration()]);
        await server.StartAsync();
        try
        {
            var config = Config(server.Services);
            var factory = Factory();
            await using var first = await factory.ConnectAsync(config);
            await using var second = await factory.ConnectAsync(config with { Id = Guid.NewGuid() });
            var discovery = new McpToolDiscovery();
            var initial = await discovery.RefreshAsync(first); var tool = Assert.Single(initial.Tools);
            Assert.Equal(tool.Descriptor.Id, Assert.Single(initial.Added));
            Assert.Matches("^mcp_" + config.Id.ToString("N") + "_[a-f0-9]{24}$", tool.Descriptor.Id);
            Assert.Equal(ToolRiskLevel.Unknown, tool.Descriptor.RiskLevel);
            Assert.Equal(new(ToolSourceKind.Mcp, config.Id.ToString("N")), tool.Descriptor.Source);
            Assert.False(tool.Descriptor.IsReadOnly); Assert.False(tool.Descriptor.RequiresDesktop);
            Assert.Empty(first.Configuration.AllowedTools);
            var repeated = await discovery.RefreshAsync(first);
            Assert.Empty(repeated.Added); Assert.Empty(repeated.Changed); Assert.Empty(repeated.Removed);
            Assert.Equal(tool.Descriptor.Id, Assert.Single(repeated.Tools).Descriptor.Id);
            Assert.NotEqual(tool.Descriptor.Id, Assert.Single((await discovery.RefreshAsync(second)).Tools).Descriptor.Id);
            // Even a permissive callback cannot classify untrusted remote metadata as safe.
            var registry = new ToolRegistry(initial.Tools, (_, _, _) => Task.CompletedTask, (_, _, _) => ValueTask.FromResult(true));
            var binding = registry.Bind(new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Instructions = "synthetic", ModelProfileId = "fake",
                AllowedToolIds = [tool.Descriptor.Id] });
            Assert.Equal("object", Assert.Single(binding.Functions).JsonSchema.GetProperty("type").GetString());
            var invalid = await binding.InvokeAsync(tool.Descriptor.Id, "bad", JsonSerializer.SerializeToElement(new { message = 42 }), CancellationToken.None);
            Assert.Equal("InvalidToolArguments", invalid.Result.ErrorCode);
            var denied = await binding.InvokeAsync(tool.Descriptor.Id, "valid", JsonSerializer.SerializeToElement(new { message = "hello" }), CancellationToken.None);
            Assert.Equal("ToolPermissionDenied", denied.Result.ErrorCode); Assert.Equal(0, calls);
            var direct = await tool.ExecuteAsync(new(JsonSerializer.SerializeToElement(new { message = "hello" })),
                new(Guid.NewGuid(), Guid.NewGuid(), "direct"), CancellationToken.None);
            Assert.Equal("McpToolReviewRequired", direct.ErrorCode); Assert.Equal(0, calls);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task RefreshDetectsAddedChangedRemovedToolsAndCanonicalizesObjectPropertyOrder()
    {
        IList<Tool> declarations = [Declaration()];
        await using var server = McpTestServer.Program.CreateHttpServer(listTools: () => declarations);
        await server.StartAsync();
        try
        {
            await using var connection = await Factory().ConnectAsync(Config(server.Services));
            var discovery = new McpToolDiscovery();
            var original = Assert.Single((await discovery.RefreshAsync(connection)).Tools);
            declarations = [Declaration(schema: "{\"required\":[\"message\"],\"properties\":{\"message\":{\"type\":\"string\"}},\"type\":\"object\"}")];
            var reordered = await discovery.RefreshAsync(connection);
            Assert.Empty(reordered.Changed); Assert.Equal(original.Version, Assert.Single(reordered.Tools).Version);
            declarations = [Declaration(description: "Changed external description"), Declaration("new_write", description: "New write tool")];
            var changed = await discovery.RefreshAsync(connection);
            Assert.Equal(original.Descriptor.Id, Assert.Single(changed.Changed)); Assert.Single(changed.Added);
            Assert.All(changed.Tools, tool => Assert.Equal(ToolRiskLevel.Unknown, tool.Descriptor.RiskLevel));
            var added = changed.Tools.Single(tool => tool.Descriptor.Id == changed.Added[0]);
            var registry = new ToolRegistry(changed.Tools, (_, _, _) => Task.CompletedTask);
            var binding = registry.Bind(new() { RunId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ModelProfileId = "fake",
                Instructions = "Synthetic new tool", AllowedToolIds = [added.Descriptor.Id] });
            Assert.Equal("ToolPermissionDenied", (await binding.InvokeAsync(added.Descriptor.Id, "new", JsonSerializer.SerializeToElement(new { message = "test" }), CancellationToken.None)).Result.ErrorCode);
            declarations = [Declaration(schema: "{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"integer\"}},\"required\":[\"message\"]}")];
            var schemaChanged = await discovery.RefreshAsync(connection);
            Assert.Equal(original.Descriptor.Id, Assert.Single(schemaChanged.Changed));
            Assert.NotEqual(original.Version, Assert.Single(schemaChanged.Tools).Version);
            declarations = [];
            var removed = await discovery.RefreshAsync(connection);
            Assert.Empty(removed.Tools); Assert.Single(removed.Removed);
        }
        finally { await server.StopAsync(); }
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"$dynamicRef\":\"https://unapproved.invalid/schema\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"https://unapproved.invalid/schema\"}}}")]
    [InlineData("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\"}")]
    public async Task UnsupportedSchemasCannotProduceAReviewableCatalogAndFailureClearsComparisonBaseline(string schema)
    {
        IList<Tool> declarations = [Declaration()];
        await using var server = McpTestServer.Program.CreateHttpServer(listTools: () => declarations);
        await server.StartAsync();
        try
        {
            await using var connection = await Factory().ConnectAsync(Config(server.Services));
            var discovery = new McpToolDiscovery();
            await discovery.RefreshAsync(connection);
            declarations = [Declaration(schema: schema)];
            var failure = await Assert.ThrowsAsync<McpConnectionException>(() => discovery.RefreshAsync(connection));
            Assert.Equal("McpToolDiscoveryInvalid", failure.Code); Assert.DoesNotContain("unapproved.invalid", failure.Message);
            declarations = [Declaration()];
            Assert.Single((await discovery.RefreshAsync(connection)).Added);
            await connection.DisposeAsync();
            await Assert.ThrowsAsync<McpConnectionException>(() => discovery.RefreshAsync(connection));
        }
        finally { await server.StopAsync(); }
    }

    private static Tool Declaration(string name = "remote.echo", string? schema = null, string description = "Untrusted description") => new()
    {
        Name = name, Description = description,
        InputSchema = JsonSerializer.Deserialize<JsonElement>(schema ?? "{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}"),
        Annotations = new() { ReadOnlyHint = true, DestructiveHint = false }
    };
    private static McpConnectionFactory Factory() => new([new HttpMcpTransportProvider(new Secrets())]);
    private static McpConnectionConfig Config(IServiceProvider services) => new()
    {
        Id = Guid.NewGuid(), Name = "Discovery fixture", Enabled = true, Approved = true, Transport = McpTransportKind.StreamableHttp,
        Http = new(new Uri(Assert.Single(services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/mcp"), AllowLoopbackHttp: true)
    };
    private sealed class Secrets : ISecretStore
    {
        public ValueTask<string> StoreAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No credentials for this fixture");
    }
}
