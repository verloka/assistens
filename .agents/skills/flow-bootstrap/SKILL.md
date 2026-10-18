---
name: flow-bootstrap
description: Discover and document end-to-end business flows in an existing repository using Flow Driven Development (FDD). Use to reverse-engineer an existing codebase, inventory user and background journeys, trace business behavior through UI/API/database/events, build docs/flows/index.md, find coverage gaps, or resume documentation in manageable batches. Documentation-only; create Draft flows without modifying code.
---

# Bootstrap existing flows

A **flow** is one complete business outcome, from its trigger through decisions, state changes, failures and asynchronous continuations to an observable result. It may span frontend, API, database, queues and third parties. Do not create a spec per endpoint or class.

## Discover and inventory

1. Read repository rules and `docs/flows/index.md`; follow `docs/flows/_template.md`.
2. Sweep the *requested scope* for UI actions/routes, API endpoints, message consumers, jobs, state transitions, integrations and tests. Ignore generated/vendor code.
3. Group entry points by business outcome. Add candidates to `index.md` as `Discovered`, with stable flow IDs and source entry-point hints. Record entry points that cannot be mapped under **Unmapped Entry Points**. Never claim full coverage from a partial scan.
4. Trace 2-5 related flows per pass (fewer when complex). Follow branches, permissions/tenant boundaries, persistence, events, retries, idempotency and cross-flow consequences. Use actual source/tests as *evidence of observed behavior*, not proof of intended behavior.
5. Create a `Draft` spec only after enough of the end-to-end path is traced. Include source references, alternate/error paths, dependencies, invariants, and numbered Given/When/Then scenarios. Link scenarios to existing tests with `[test: repo/relative/path]`; otherwise use `[untested]`. Explicitly record unknowns and suspected bugs.
6. Update the index (`Discovered` -> `Draft`), then inspect reverse consumers of shared code/events. Reconcile unmapped entry points and flow dependencies. Inspect index entries, dependency references and evidence links directly; repair documentation errors only.
7. Resume from the index next time. Avoid re-reading the entire project when the area and gaps are already recorded. Optional parallel exploration is fine only if tools support it; no extra agents are required.

## Limits and handoff

- **Documentation-only:** never edit application code, tests, data or existing approved behavior.
- Never promote a Draft to `Active`: this requires explicit human approval. Do not silently rewrite Active docs to match buggy code.
- Report documented flows, candidates remaining, unmapped entry points, coverage limits, unknowns and suspected defects. Leave a useful partial index even if the scan is interrupted.
