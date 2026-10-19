using ModelContextProtocol.Client;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Net.Http.Headers;

namespace PersonalAgent.Mcp;

public sealed class HttpMcpTransportProvider(ISecretStore secrets, Func<HttpMessageHandler>? createHandler = null) : IMcpTransportProvider
{
    public McpTransportKind Transport => McpTransportKind.StreamableHttp;

    public async ValueTask<IClientTransport> CreateAsync(McpConnectionConfig configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.Enabled || !configuration.Approved || configuration.Transport != Transport || configuration.Http is not { } config)
            throw Error("McpConnectionNotApproved", "The HTTP MCP destination has not been approved by the owner.");
        if (config.Endpoint is not { IsAbsoluteUri: true } endpoint || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.Scheme != "https" && !(config.AllowLoopbackHttp && endpoint.Scheme == "http" && endpoint.IsLoopback))
            throw Error("McpConfigurationInvalid", "The HTTP MCP endpoint is invalid.");
        string? token = null;
        try
        {
            if (config.BearerSecretRef is { } reference)
            {
                token = await secrets.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token)) throw Error("McpSecretUnavailable", "The configured MCP credential is unavailable.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (McpConnectionException) { throw; }
        catch (Exception) { throw Error("McpSecretUnavailable", "The configured MCP credential store is unavailable."); }
        cancellationToken.ThrowIfCancellationRequested();
        // Tests/trusted composition may supply an owned handler; settings cannot redirect credentials or change transport behavior.
        var handler = createHandler?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null };
        var http = new HttpClient(new EndpointHandler(endpoint, token, handler)) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "mcp-" + configuration.Id.ToString("N"),
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = configuration.ConnectTimeout,
                MaxReconnectionAttempts = 0,
                EnableStandaloneGetStream = false,
                OwnsSession = true
            }, http, ownsHttpClient: true);
        }
        catch { http.Dispose(); throw; }
    }

    private sealed class EndpointHandler(Uri endpoint, string? bearer, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private string? token = bearer;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != endpoint)
                throw Error("McpDestinationDenied", "The MCP request attempted to leave its approved endpoint.");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;
                var status = (int)response.StatusCode;
                response.Dispose();
                throw status switch
                {
                    >= 300 and < 400 => Error("McpRedirectDenied", "MCP redirects are not approved; no credential was forwarded."),
                    401 or 403 => Error("McpAuthenticationFailed", "The MCP server rejected authentication or access."),
                    404 => Error("McpEndpointUnavailable", "The configured MCP endpoint or session is unavailable."),
                    429 => Error("McpRateLimited", "The MCP server is rate limited; no request was replayed."),
                    >= 500 => Error("McpServerUnavailable", "The MCP server is unavailable; no request was replayed."),
                    _ => Error("McpRequestRejected", "The MCP server rejected the request; no request was replayed.")
                };
            }
            finally { request.Headers.Authorization = null; }
        }
        protected override void Dispose(bool disposing) { token = null; base.Dispose(disposing); }
    }
    private static McpConnectionException Error(string code, string message) => new(code, message);
}
