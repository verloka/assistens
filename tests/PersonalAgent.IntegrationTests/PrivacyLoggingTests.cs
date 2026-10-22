using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using PersonalAgent.App;
using System.Globalization;
using System.IO;

namespace PersonalAgent.IntegrationTests;

public sealed class PrivacyLoggingTests
{
    [Theory]
    [InlineData("PersonalAgent.App")]
    [InlineData("ModelContextProtocol")]
    [InlineData("System.Net.Http.HttpClient")]
    public void StandardFormatterNeverRendersBodiesImagesSecretsScopesOrExceptionPayloads(string category)
    {
        const string secret = "synthetic-private-key-DO-NOT-LOG";
        const string body = "synthetic private message body";
        const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB";
        var runId = Guid.NewGuid();
        IReadOnlyList<KeyValuePair<string, object?>> fields = new Dictionary<string, object?>
        {
            ["RunId"] = runId, ["NativeError"] = 5, ["InputTokens"] = 123L,
            ["Message"] = body, ["ImageBytes"] = new byte[] { 1, 2, 3 }, ["Screenshot"] = image,
            ["Secret"] = secret, ["ToolCallId"] = secret, ["{OriginalFormat}"] = body + secret
        }.ToArray();
        var error = new InvalidOperationException(secret + body + image, new IOException(body));
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(LogLevel.Error, category + "." + secret,
            new EventId(117, body), fields, error, (_, _) => throw new InvalidOperationException("Unsafe formatter must never execute"));
        var scopes = new LoggerExternalScopeProvider(); using var scope = scopes.Push(body + secret + image);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new PrivacyConsoleFormatter().Write(entry, scopes, writer);
        var output = writer.ToString();
        Assert.Contains("EventId=117", output); Assert.Contains("RunId=" + runId, output); Assert.Contains("NativeError=5", output);
        Assert.Contains("InputTokens=123", output); Assert.Contains("FailureType=InvalidOperationException", output);
        Assert.DoesNotContain(secret, output); Assert.DoesNotContain(body, output); Assert.DoesNotContain(image, output);
        Assert.DoesNotContain("OriginalFormat", output); Assert.DoesNotContain("InnerException", output);
    }

    [Fact]
    public void DefaultHostUsesOnlyPrivacyConsoleProviderAndNoRawDebugOrEventLogProvider()
    {
        using var host = AppHost.CreateBuilder([]).Build();
        Assert.IsType<ConsoleLoggerProvider>(Assert.Single(host.Services.GetServices<ILoggerProvider>()));
        Assert.Equal(PrivacyConsoleFormatter.FormatterName,
            host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
        Assert.Contains(host.Services.GetServices<ConsoleFormatter>(), formatter => formatter is PrivacyConsoleFormatter);
    }
}
