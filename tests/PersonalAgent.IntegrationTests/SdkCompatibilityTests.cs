using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace PersonalAgent.IntegrationTests;

public sealed class SdkCompatibilityTests
{
    [Fact]
    public void AgentAndMcpSdkTypesLoadTogetherInTheDesktopDependencyGraph()
    {
        Assert.True(typeof(AIAgent).IsAssignableFrom(typeof(ChatClientAgent)));
        Assert.True(typeof(IChatClient).IsInterface);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(McpClient)));
        Assert.True(typeof(IClientTransport).IsAssignableFrom(typeof(StdioClientTransport)));
        Assert.True(typeof(IClientTransport).IsAssignableFrom(typeof(HttpClientTransport)));
        Assert.NotNull(typeof(AgentSession).Assembly.GetName().Version);
    }
}
