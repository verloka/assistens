# Personal AI Desktop Agent — implementable backlog

**125 granular tasks across 15 PR streams**, traced to [the original development plan](docs/DEVELOPMENT_PLAN_v1.1.md). Each task contains dependencies, concrete acceptance conditions, verification ideas and flags for external/manual verification.

This package provides planning **and persistent autonomous continuation**, not the application implementation itself. On the first run all tasks are `pending`.

## Files

- `docs/DEVELOPMENT_PLAN_v1.1.md` — complete product/engineering specification.
- `TASKS.md` — detailed human-readable work items grouped by PR.
- `tasks.json` — machine-readable immutable catalog + dependency DAG.
- `progress/state.json` — authoritative, version-controlled status/evidence (initially all pending).
- `progress/CHECKPOINT.md` — short current-session handoff.
- `progress/LOG.md` — append-only development journal.
- `AGENTS.md` + `CLAUDE.md` — rules for Codex/Claude Code.
- `scripts/taskctl.ps1` — dependency-aware task state CLI (PowerShell 5.1+, no modules).

## FDD for Codex — already installed

The archive includes Flow Driven Development (FDD) from [`verloka/fdd`](https://github.com/verloka/fdd), pinned to commit `9f4e6e7bc4402e845894eeac623b534e797daa2f` (Oct 11, 2026). **No extra installation step, Python, or PowerShell bootstrap is required for FDD.**

- `.agents/skills/flow-bootstrap/SKILL.md` — `$flow-bootstrap`: inventory/tracing of **existing implementation** (docs only).
- `.agents/skills/flow-develop/SKILL.md` — `$flow-develop`: development guided by complete business flow outcomes and affected reverse dependencies.
- `.agents/skills/flow-verify/SKILL.md` — `$flow-verify`: diff + scenario + regression verification.
- `AGENTS.md` — upstream Codex FDD policy appended to original project instructions and integrated into the task loop.
- `docs/flows/index.md`, `_template.md`, seven **Draft** flow documents — starting design proposals from agreed requirements (NOT implemented/approved Active contracts).
- `third_party/fdd/LICENSE` and `UPSTREAM.md` — attribution, pinned version and hash provenance.

**No migration of an old development methodology.** This is a new project. Existing `tasks.json` and `progress/state.json` are unchanged; FDD is used within each behavior-changing backlog task, not as another task tracker.

Recommended per-task flow: `taskctl next` → identify impacted flow IDs → `$flow-develop` → implement + run real tests → `$flow-verify` → `taskctl done` with evidence. The actual Codex skill invocation syntax may be `$flow-develop`, or you can instruct Codex to read its SKILL.md.

The FDD skills were installed **for Codex only**; the existing `CLAUDE.md` remains in the archive, but no `.claude/skills/` were added.

## How to start

On Windows, unpack in the new project repository root, then open the folder in Codex or Claude Code. Tell the coding agent:

> Read AGENTS.md. Implement the project according to tasks.json. Continue the first available task, validate and record completion, then move to the next. Never mark a task done without evidence.

Check the initial task:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action doctor
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action next
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action start
```

After implementing and **actually validating** PA-001:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action done -Id PA-001 -Evidence 'solution files plus commit sha' -Test 'dotnet build: success on Windows'
```

If blocked by a real missing external prerequisite:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action block -Id PA-006 -Note 'Requires Anthropic/OpenAI API test key'
```

Then `next` will choose **another** dependency-ready pending task if available. After the prerequisite is provided, `unblock -Id PA-006` returns it to the queue. Blocked tasks are never treated as completed.

If the owner explicitly authorizes continuing past a particular blocked dependency,
record that decision on the dependent task without changing the catalog or accepting
the missing verification:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action start -Id PA-114 -DeferDependency PA-113 -Note 'Owner explicitly deferred remaining PA-113 native checks to final review and authorized PA-114'
```

This accepts only the named declared blocked edge and stores its reason/time in
`deferred_dependencies`. It requires an owner decision in `Note`; ordinary starts
still require completed dependencies. PA-113 remains blocked. Final PA-125 completion
requires every other task done, including any explicitly deferred prerequisites.

Report current progress:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/taskctl.ps1 -Action report
```

## Tracking semantics

- `pending`: untouched, can start when all dependencies `done` or a particular blocked edge was explicitly deferred by the owner as above.
- `in_progress`: work underway. Always resumed before starting new work.
- `blocked`: cannot verify/complete now; reason required. Work on another ready task.
- `done`: independently verified; evidence and test outcome required.

**No fake completion.** Reaching the end of the catalog requires all user/external verification gates (real Windows tests, supported model access, Jira/ADO permissions, publish and E2E). Keep those blocked until verified.

The tracker is designed for **one coding agent at a time**. Run the CLI from a git repository and commit source and tracker updates together; concurrent writers to one `progress/state.json` are not supported.

## Recommended developer workflow

1. Read `progress/CHECKPOINT.md` and the most recent `progress/LOG.md` entries.
2. `doctor` validates task graph and state.
3. `next` returns an in-progress task first, otherwise first eligible pending task (by milestone/sequence).
4. Implement, build, run tests, record real evidence.
5. `done` then update checkpoint + log; commit.
6. Loop. Manual/environment tasks without access: `block`, keep going where dependencies allow.

Do not edit `tasks.json` to mark completion. Edit it only during intentional backlog/version changes and preserve all IDs and their state mappings.
