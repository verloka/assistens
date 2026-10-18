# Build and test

Requires Windows and the .NET SDK selected by `global.json` (currently 10.0.401).
No commercial API key, tenant credentials or bot token is needed for the current tests.

From the repository root, run the full verification with one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

The script restores in locked mode, builds, and runs all test projects. A failing
command stops the script with an error. Use `-Configuration Release` for Release.

Equivalent individual commands:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
```

Run a focused group with `dotnet test --filter DesktopHostTests` or
`dotnet test --filter HealthScreenTests`.

- `PersonalAgent.UnitTests`: module boundary and reference cycle checks.
- `PersonalAgent.IntegrationTests`: actual Generic Host and SDK loading; host
  probes/fakes (`ProbeService`, `CancellableWorker`, recording logger) cover
  cancellation and failure cleanup. WPF tests load the real XAML on STA threads
  and pump the dispatcher without displaying windows or generating input.
- `PersonalAgent.E2ETests`: reserved scaffold; no tests yet. A successful command
  or a "no tests available" message is not business E2E acceptance.

Add scenario links to existing FDD Drafts only when corresponding tests exist;
never promote them to Active without explicit approval. Keep live/manual provider,
device and corporate checks separate from fake/local evidence. The ten E2E cases
and MVP release gate remain unverified until their own backlog tasks are completed.
