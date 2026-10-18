# SDK baseline

Date: 2026-10-18. Scope: PA-004, dependency/API compatibility only.

Use .NET SDK 10.0.401 (`global.json`), central direct versions in
`Directory.Packages.props`, and committed `packages.lock.json` files for resolved
transitive versions. Verify reproducibility with `dotnet restore --locked-mode`.

| Package | Version | Owning module |
| --- | --- | --- |
| Microsoft.Agents.AI | 1.25.0 | Agent |
| Microsoft.Extensions.AI | 10.10.0 | Agent |
| ModelContextProtocol | 2.2.0 | Mcp |
| Microsoft.Extensions.Hosting | 10.0.12 | App |

MAF's package metadata requests Extensions.AI 10.10.0 and AI.Abstractions
10.10.1; preserve that combination instead of independently upgrading the AI
pipeline. Hosting 10.0.12 aligns with MAF's Microsoft.Extensions dependencies.
These are stable packages; no prerelease is needed for this baseline. Core has
no SDK or infrastructure package reference.

`SdkCompatibilityTests.cs` loads the concrete agent/session and client transport
types together. Restore/build and the host/health regressions verify the desktop
dependency graph. This is not evidence of model calls, tool approval/resume or
MCP transport execution: those belong to PA-006/PA-007 and later adapters.

Sources: [MAF package](https://www.nuget.org/packages/Microsoft.Agents.AI/1.25.0),
[MAF docs](https://learn.microsoft.com/en-us/agent-framework/),
[AI package](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0),
[official MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk),
[MCP package](https://www.nuget.org/packages/ModelContextProtocol/2.2.0).

Run `dotnet restore --locked-mode`, `dotnet build --no-restore`, and `dotnet test --no-build`
after intentional dependency upgrades. Never infer runtime provider behavior from
successful compilation.
