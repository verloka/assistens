namespace PersonalAgent.Core.Agents;

public sealed record ModelCapabilities(bool SupportsImages = false, bool SupportsToolCalling = false);
public sealed record ModelGenerationSettings(float? Temperature = null, int? MaxOutputTokens = null);
public sealed record ModelPricing(decimal InputPerMillionTokens, decimal OutputPerMillionTokens, string Currency);

// Persist/export only this configuration; never put credential values in it.
public sealed record ModelProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string SecretRef { get; init; }
    public ModelCapabilities Capabilities { get; init; } = new();
    public ModelGenerationSettings GenerationSettings { get; init; } = new();
    public ModelPricing? Pricing { get; init; }
}
