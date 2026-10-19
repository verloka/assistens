# FDD-004 — MCP tool discovery and authorized invocation

Status: Draft

## Trigger / Outcome
- Initiator: the configured MCP connection is enabled or the model requests a tool during task execution.
- Outcome: approved tool returns a result or meaningful failure; no unapproved new tool is exposed or executed.

## Inputs
- MCP server configuration, authentication, transport (stdio / Streamable HTTP), discovered tool schema, call arguments and run permission policy.

## Outputs
- Available tool descriptions and structured results; refusal/timeout/disconnect errors with auditable run references.

## Flow
1. Establish an explicitly configured MCP connection and authenticate as applicable.
2. Discover tools and schemas, treat server metadata as untrusted until the connection is approved.
3. Filter tools through an allowlist; deny newly discovered tools until permission is granted.
4. When model invokes an allowed tool, validate arguments, apply FDD-005 authorization for sensitive effects and enforce timeouts.
5. Report sanitized result to agent; if unavailable, allow model to select another authorized method (possibly GUI).

## Validations
- Connection trust, user authorization, schema validation, per-tool permission, destination and side-effect controls.

## Invariants
- Tool discovery does not imply write permission.
- An untrusted tool response cannot override system/approval rules.
- Runtime can add an MCP server without changes to business Task Runner code.

## Failures / Side Effects
- Auth errors, unsupported transports, schema mismatches, timeouts and connection drops do not bypass policy.
- External side effects are gated by FDD-005 and must not be blindly replayed.

## Dependencies
- Flow: FDD-005 — tool approval.

## Scenarios
- S1: Given an MCP server with a newly added write tool, when tools are discovered, then it is denied by default. [test: tests/PersonalAgent.IntegrationTests/McpDiscoveryTests.cs]
- S2: Given an allowed read tool, when it is called with valid arguments, then the model receives the result. [untested]
- S3: Given a tool timeout, when the agent handles failure, then the UI and run state remain consistent. [untested]

- PA-069: McpToolDiscovery lists actual SDK declarations, maps self-contained object schemas and connection-scoped stable provider-safe IDs, and detects added/changed/removed tools with canonical metadata fingerprints. Discovery records and server read-only hints grant no permissions: declarations register in the existing ToolRegistry with Unknown risk, and direct execution remains review-required until an owner-approved execution binding exists (PA-070). New tools, metadata and schema changes therefore do not inherit approval. App registers the discovery service without startup network calls. [test: tests/PersonalAgent.IntegrationTests/McpDiscoveryTests.cs] covers live loopback SDK discovery, registry schema/permission denial, new-tool refusal, connection namespace isolation, versions and rejected remote schema references. S1 scoped local-server acceptance passed; S2 execution and S3 run/UI failure handling remain untested. All flows Draft.

## Implementation References
- PA-068: HttpMcpTransportProvider creates only approved explicit Streamable HTTP connections. Each owned HttpClient scopes bearer auth to the exact configured HTTPS endpoint (opt-in loopback HTTP for tests), disables redirect/cookie/ambient credentials and SDK stream reconnection, drops raw HTTP error bodies and returns sanitized status codes. McpClientConnection request methods bound/cancel list/call and never replay unknown outcomes; explicit factory reconnect creates a fresh client/discovery or initialize sequence. [test: tests/PersonalAgent.IntegrationTests/McpHttpTests.cs] exercises real loopback list/call/auth/reconnect, actual cancelled/timed-out tool and HTTP status/redirect/credential refusals. SDK2.2 can use stateless server/discover with no session ID. No corporate server/model or complete S1-S3 acceptance; future manager/discovery/permission tasks remain separate and all flows Draft.
- PA-067: StdioMcpTransportProvider accepts the owner-approved explicit existing executable/argv/working directory only, resolves credentials into that child's permitted environment, and never inherits unrelated secrets. It uses direct ProcessStartInfo.ArgumentList (hidden window, no shell) plus the pinned SDK StreamClientTransport; the pinned SDK's Windows stdio wrapper uses cmd.exe. Stderr is drained in bounded chunks and only a character count is logged; process cleanup closes stdin and bounds wait/whole-child-tree termination. [test: tests/PersonalAgent.IntegrationTests/McpStdioTests.cs] executes the actual local fixture, preserves literal shell punctuation, verifies scoped environment/no raw-secret logs, real list/call, child exit, handshake cancellation/timeout cleanup and refusal before process/credential access. Eight local process cases; no corporate service/model/tool-approval business acceptance. Complete S1-S3 stay untested/Draft.
- PA-066: Core has SDK-neutral McpConnectionConfig/stdio/HTTP settings, stable connection ID, explicit enabled/owner-approved flags, tool allowlist and scoped opaque secret references. McpConnectionFactory selects a DI IMcpTransportProvider, defensively snapshots settings, rejects unapproved/invalid destinations before the transport boundary, bounds connection initialization, sanitizes failures and owns SDK/transport cleanup. Default App registers only the factory; concrete production transports follow PA-067/068 and no connection is made at startup. [test: tests/PersonalAgent.IntegrationTests/McpConnectionConfigTests.cs] verifies two configured connections to a real loopback SDK server using a test-only transport provider, unchanged allowlists, default denial, scoped settings, cancellation/timeout and failed-handshake cleanup. Scoped connection foundation only; complete S1-S3 and tool/model/approval integration remain untested/Draft.
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §8; backlog PR-09, PR-10.
- SDK diagnostic only (PA-007): `tests/PersonalAgent.IntegrationTests/McpTransportSpikeTests.cs` and `tests/PersonalAgent.McpTestServer/Program.cs` verify initialization, schema discovery and an in-memory echo call over stdio and loopback Streamable HTTP, including resource cleanup. This does not implement application trust/allowlists, approval, model integration or run failure handling; complete S1-S3 remain `[untested]`. Reproduction: `tests/README.md`.

## Unknowns
- Which Jira/ADO/GitLab endpoints are accessible and provider permissions must be verified at deployment.
