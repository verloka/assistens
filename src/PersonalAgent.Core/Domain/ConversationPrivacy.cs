using System.Text.RegularExpressions;

namespace PersonalAgent.Core.Domain;

// Defense in depth for conversation data, not a classifier for arbitrary confidential prose.
public static partial class ConversationPrivacy
{
    public static string Filter(string text) => Credentials().Replace(text, "[redacted]");

    public static ConversationMessage Filter(ConversationMessage message) => message with
    {
        Content = Filter(message.Content),
        ErrorCode = message.ErrorCode is { } error ? Filter(error) : null,
        Sources = FilterSources(message.Sources)
    };

    public static IReadOnlyList<SourceLink> FilterSources(IEnumerable<SourceLink> sources) => sources
        .Where(source => source.Url.IsAbsoluteUri && source.Url.Scheme is "http" or "https")
        .Select(source =>
        {
            var uri = new UriBuilder(source.Url) { UserName = "", Password = "" };
            return new SourceLink(Filter(source.Title), new Uri(Filter(uri.Uri.AbsoluteUri)));
        }).ToArray();

    [GeneratedRegex("""(?is)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----|\b(?:sk-(?:proj-|ant-)?[a-zA-Z0-9_-]{12,}|[0-9]{5,}:[a-zA-Z0-9_-]{20,})\b|\bBearer\s+[a-zA-Z0-9._~+/%=-]+|[a-zA-Z][a-zA-Z0-9+.-]*://[^\s/@]+:[^\s/@]+@|["']?\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|client[_-]?secret|authorization)["']?\s*[:=]\s*(?:"[^"]*"|'[^']*'|(?:Bearer\s+)?[^\s&,;<>]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Credentials();
}
