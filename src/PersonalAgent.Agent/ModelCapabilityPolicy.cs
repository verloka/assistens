using PersonalAgent.Core.Agents;

namespace PersonalAgent.Agent;

public static class ModelCapabilityPolicy
{
    public static void DemandAgentSupport(ModelProfile profile, bool requiresDesktop)
    {
        if (profile.Capabilities?.SupportsToolCalling != true)
            throw new ModelProviderException("ToolCallingRequired", "The selected model is not configured for function calling; select a compatible agent profile.");
        if (requiresDesktop && !profile.Capabilities.SupportsImages)
            throw new ModelProviderException("VisionRequired", "The selected model cannot use desktop screenshots. Select an image-capable profile or a tools-only task.");
    }
}
