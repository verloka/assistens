using Microsoft.Extensions.AI;
using PersonalAgent.Core.Agents;

namespace PersonalAgent.Agent;

// SDK-facing contract belongs in Agent, not Core.
public interface IModelProvider
{
    string ProviderId { get; }
    Task<ModelConnection> OpenAsync(ModelProfile profile, CancellationToken cancellationToken);
    Task<ModelConnectionTest> TestConnectionAsync(ModelProfile profile, CancellationToken cancellationToken);
}

public sealed class ModelConnection(IChatClient client, ModelProfile profile) : IDisposable
{
    public IChatClient Client { get; } = client;
    public ModelProfile Profile { get; } = profile;
    public void Dispose() => Client.Dispose();
}

public enum ModelProbeDiagnostic { ToolsRequestFailed, ImageRequestFailed, ToolsOutputLimit, ImageOutputLimit, ToolsResponseInvalid, ImageResponseInvalid }
public sealed record ModelConnectionTest(bool Connected, string? ErrorCode, string Message, ModelCapabilities? VerifiedCapabilities = null,
    ModelProbeDiagnostic? ProbeDiagnostic = null);
public sealed class ModelProviderException(string code, string message, ModelProbeDiagnostic? probeDiagnostic = null) : Exception(message)
{
    public string Code { get; } = code;
    public ModelProbeDiagnostic? ProbeDiagnostic { get; } = probeDiagnostic;
}
