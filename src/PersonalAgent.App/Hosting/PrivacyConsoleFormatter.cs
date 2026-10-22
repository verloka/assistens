using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System.Globalization;
using System.IO;

namespace PersonalAgent.App;

// Standard output is metadata only, including framework/SDK failures and scopes.
// Never call arbitrary state formatters or render exception messages/stack traces.
public sealed class PrivacyConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "privacy";
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "RunId", "ConversationId", "ToolCallId", "NotificationId", "McpConnectionId", "ConnectionId", "ProcessId",
        "Duration", "LeaseGeneration", "DesktopLeaseGeneration", "InputTokens", "OutputTokens", "Attempt", "NativeError", "StderrCharacters", "OccurrenceUtc"
    };

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var category = entry.Category;
        var module = new[] { "App", "Core", "Agent", "Computer", "Mcp", "Persistence", "Channels" }
            .FirstOrDefault(value => category == "PersonalAgent." + value || category.StartsWith("PersonalAgent." + value + ".", StringComparison.Ordinal)) ?? "Framework";
        writer.Write($"{DateTimeOffset.UtcNow:O} {entry.LogLevel} {module} EventId={entry.EventId.Id}");
        if (entry.State is IEnumerable<KeyValuePair<string, object?>> fields)
        {
            try
            {
                foreach (var (key, value) in fields)
                {
                    if (Keys.Contains(key) && value is Guid or int or long or uint or ulong or bool or decimal or double or TimeSpan or DateTimeOffset)
                        writer.Write($" {key}={Convert.ToString(value, CultureInfo.InvariantCulture)}");
                    else if (key == "ProviderId" && value is "openai" or "anthropic" or "fake")
                        writer.Write($" ProviderId={value}");
                    else if (key is "ToolId" or "ProviderId" && value is string identifier)
                        writer.Write($" {key}={RunDiagnosticsService.Identifier(identifier)}");
                    else if (key == "ErrorCode" && value is RunDiagnosticCode code && Enum.IsDefined(code))
                        writer.Write($" ErrorCode={code}");
                    else if (key == "Status" && value is PersonalAgent.Core.Domain.RunStatus or PersonalAgent.Core.Channels.NotificationDeliveryStatus)
                        writer.Write($" Status={value}");
                }
            }
            catch (Exception) { writer.Write(" MetadataUnavailable"); }
        }
        if (entry.Exception is not null) writer.Write(" FailureType=" + entry.Exception.GetType().Name);
        writer.WriteLine();
    }
}
