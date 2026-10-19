using Microsoft.Extensions.Hosting;
using PersonalAgent.Mcp;

namespace PersonalAgent.App;

internal sealed class McpLifecycleService(McpConnectionManager manager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken) => await manager.DisposeAsync().ConfigureAwait(false);
}
