# Agent checkpoint
- Last completed task: PA-077 (tool permission rules per run/connection).
- Current task: none; next PA-078 durable pending approvals.
- Modified files: Core ToolPermissionPolicy/ToolDescriptor version; MCP discovery/execution version sharing; unit/integration policy tests; FDD004/005; progress.
- Verified build/test: scripts/verify.ps1 locked restore/build0warnings/errors;98unit+528integration+3localfake=629passed. Targeted policy7unit+1actual SDK integration passed. Doctor/diff/FDD actual semantic review passed.
- Decisions: explicit run tool/source/risk/version allow/deny intersects immutable category context and owner connection grants. Deny wins, no inheritance/wildcards. Sensitive scope RequiresApproval is not consent. Existing MCP current-version/connection allowlist gates retained, default production tool gate closed. All flows Draft; full approval business scenarios untested.
- Runtime blockers:075 missing approved corporate endpoint/credential/org authorization/test ticket;065 Claude deferred;026/030 multi-monitor hardware.059 actual OpenAI acceptance done.
- Next instruction: taskctl next/start078; inspect existing SQLite/version/CAS conventions and Run WaitingForApproval. Add minimal Core durable approval contract + one SQLite table, exact validated ToolCallId/payload/run/owner/expiry/version binding and replay rejection; no write dispatch yet (079/081), no unsolicited architecture.
