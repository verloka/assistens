using ModelContextProtocol.Client;
using PersonalAgent.Core.Agents;
using PersonalAgent.Mcp;

namespace PersonalAgent.IntegrationTests;

public sealed class McpConnectionDeadlineTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TransportClosureErrorAfterCancellationKeepsCallerOrDeadlineClassification(bool callerCancel)
    {
        var provider = new ClosingProvider(); var factory = new McpConnectionFactory([provider]);
        using var caller = new CancellationTokenSource();
        var attempt = factory.ConnectAsync(new() { Id = Guid.NewGuid(), Name = "Synthetic closing transport", Enabled = true, Approved = true,
            Transport = McpTransportKind.StreamableHttp, Http = new(new("https://example.invalid/mcp")),
            ConnectTimeout = TimeSpan.FromMilliseconds(callerCancel ? 5000 : 50) }, caller.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancel)
        {
            caller.Cancel(); var error = await Assert.ThrowsAsync<OperationCanceledException>(() => attempt);
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else Assert.Equal("McpConnectionTimedOut", (await Assert.ThrowsAsync<McpConnectionException>(() => attempt)).Code);
        Assert.Equal(1, provider.Calls);
    }
    private sealed class ClosingProvider : IMcpTransportProvider
    {
        public int Calls;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public McpTransportKind Transport => McpTransportKind.StreamableHttp;
        public async ValueTask<IClientTransport> CreateAsync(McpConnectionConfig configuration, CancellationToken ct)
        {
            Calls++; Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { throw new System.IO.IOException("Synthetic transport closed after cancellation"); }
            throw new InvalidOperationException();
        }
    }
}
