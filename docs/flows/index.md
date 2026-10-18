# Business Flow Index

A flow is one **complete user-observable outcome**, not a class, tool, API or database table.

- `Discovered`: possible flow, not traced; documentation optional.
- `Draft`: proposed or traced flow, **not human-approved**.
- `Active`: explicitly human-approved, authoritative behavior contract.

**Initial state:** all entries below are **design Drafts** derived from `docs/DEVELOPMENT_PLAN_v1.1.md` and the agreed project scope. No application implementation or passing test is claimed. They are **not Active** until the user explicitly approves them. Update source references and `[test: ...]` links as development adds code and verified tests.

| Flow ID | Status | Trigger -> outcome | Depends on (flow IDs) | Entry-point hints (design only) |
| --- | --- | --- | --- | --- |
| FDD-001 | Draft | User submits task -> execution and durable final result | FDD-002, FDD-004, FDD-005, FDD-006 | WPF New Task; inbound channel; task runner |
| FDD-002 | Draft | GUI use or user takeover -> exclusively controlled or released desktop | — | Computer tools; DesktopLease; global hotkey |
| FDD-003 | Draft | Scheduled occurrence -> safe single run, deferral or skip | FDD-001, FDD-002, FDD-006 | Scheduler; idle detection; countdown |
| FDD-004 | Draft | Model requests MCP tool -> policy-checked result or failure | FDD-005 | MCP connection manager; tool registry |
| FDD-005 | Draft | Sensitive action requested -> explicit approval or denial | — | Permission policy; approvals UI/channel |
| FDD-006 | Draft | Run yields result or follow-up -> delivered message or continued dialog | — | Notification outbox; Telegram; desktop UI |
| FDD-007 | Draft | App restarts during execution -> recover safely without duplicate effects | FDD-001, FDD-003, FDD-005, FDD-006 | SQLite state; startup recovery |

## Unmapped Entry Points

- Implemented diagnostic entry point: `src/PersonalAgent.App/MainWindow.xaml` shows host status and registered hosted services, with a local asynchronous test operation. It does not submit a business task or call model/computer/MCP/channel tools; no separate business flow is warranted.
- After implementing each milestone, inspect new WPF actions, Telegram commands, scheduled triggers, MCP tools and background workers. Record any entry point not covered by an indexed flow here before closing the task.

## Coverage Notes

- Areas inspected: product requirements in `docs/DEVELOPMENT_PLAN_v1.1.md`; backlog catalog; preliminary FDD flow contracts; application host and diagnostic health screen. `tests/PersonalAgent.IntegrationTests/HealthScreenTests.cs` covers actual XAML bindings, dispatcher responsiveness and shutdown cancellation; it does not verify the business scenarios below.
- Areas **not** implemented/verified: task execution, persistence/recovery, desktop automation, model/MCP/channel integrations and business E2E outcomes. PA-002 startup/shutdown alone has a user-observed Windows smoke result in `progress/LOG.md`.
- All scenarios are `[untested]` until associated automated/manual tests have actually been implemented and verified.
- PA-006's `tests/PersonalAgent.IntegrationTests/MafToolApprovalSpikeTests.cs` verifies the SDK-only local function and approval handshake with a scripted model. It does not complete FDD-005 or its reverse consumers FDD-001/FDD-004/FDD-007; their business scenario coverage remains untested.
- Current Drafts are proposed contracts only. Validate against source code and explicit user approval before changing status to `Active`.
- Flow relationships are directional; when a dependency changes, inspect all **reverse consumers** in the index.
- FDD documentation and `tasks.json` serve different purposes: flows capture outcomes/invariants, tasks capture implementation order. `progress/state.json` remains the sole task-status authority.
