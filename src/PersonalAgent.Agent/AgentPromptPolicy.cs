namespace PersonalAgent.Agent;

public static class AgentPromptPolicy
{
    public const int Version = 1;
    private static readonly Lazy<string> instructions = new(() => string.Join("\n\n",
        new[] { "system.md", "computer-use.md", "tool-selection.md", "conversation.md", "summary.md" }.Select(Read)));
    public static string Instructions => instructions.Value;

    private static string Read(string file)
    {
        using var stream = typeof(AgentPromptPolicy).Assembly.GetManifestResourceStream("PersonalAgent.Agent.Prompts." + file)
            ?? throw new InvalidOperationException("Bundled application policy is missing.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidOperationException("Bundled application policy is empty.") : text;
    }
}
