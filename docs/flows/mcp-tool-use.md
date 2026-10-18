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
- S1: Given an MCP server with a newly added write tool, when tools are discovered, then it is denied by default. [untested]
- S2: Given an allowed read tool, when it is called with valid arguments, then the model receives the result. [untested]
- S3: Given a tool timeout, when the agent handles failure, then the UI and run state remain consistent. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §8; backlog PR-09, PR-10.
- SDK diagnostic only (PA-007): `tests/PersonalAgent.IntegrationTests/McpTransportSpikeTests.cs` and `tests/PersonalAgent.McpTestServer/Program.cs` verify initialization, schema discovery and an in-memory echo call over stdio and loopback Streamable HTTP, including resource cleanup. This does not implement application trust/allowlists, approval, model integration or run failure handling; complete S1-S3 remain `[untested]`. Reproduction: `tests/README.md`.

## Unknowns
- Which Jira/ADO/GitLab endpoints are accessible and provider permissions must be verified at deployment.
