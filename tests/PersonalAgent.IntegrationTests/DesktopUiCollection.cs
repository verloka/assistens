namespace PersonalAgent.IntegrationTests;

// WPF's pack-resource cache is process-wide; separate STA dispatchers must not load BAML concurrently.
[CollectionDefinition("Desktop UI", DisableParallelization = true)]
public sealed class DesktopUiCollection;
