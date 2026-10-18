---
name: flow-develop
description: Implement application changes using Flow Driven Development (FDD) when a task may affect end-to-end user or business behavior. Find relevant Active specs in docs/flows, follow reverse dependencies, preserve unrequested rules, update only explicitly changed behavior before coding, and maintain scenario-to-test traceability. Use for new features, changes, bug fixes and flow-affecting refactors.
---

# Develop without breaking flows

`docs/flows/` captures complete business outcomes, not implementations. `Active` means human-approved expected behavior. `Draft` is an unapproved observation or proposal. Explicit user requirements can change relevant Active rules, **not unrelated rules**.

## Procedure

1. **Find the impact.** Use `docs/flows/index.md` and search by business terms, entry points, touched symbols, events, tables and tests. Follow dependencies **and reverse consumers**, especially for shared services and schemas. Source paths in specs are search hints, not a complete call graph.
2. **Read affected flows.** Trace end to end, including errors, state transitions, async follow-ups and invariants. If documentation is absent, inspect existing code/tests; missing specs do not permit unrelated behavior changes.
3. **Decide the documentation change before editing code:**
   - New business outcome: add a Draft flow from explicit requirements, link it in the index, then implement. Human approval is still needed for `Active`.
   - Explicitly requested behavior change: edit **only affected clauses/scenarios** of impacted Active specs first; retain every other rule and scenario. No separate proposal or delta document.
   - Refactor, optimization or bug fix restoring the spec: preserve spec behavior unchanged.
   - Undocumented existing behavior: record a Draft observation when useful; preserve unrelated behavior.
4. Clarify conflicts with Active invariants or material ambiguity when possible. If clarification is unavailable, implement only the unambiguous safe portion and report what remains undecided. Never alter a spec to rationalize the code.
5. Implement the smallest scoped change. Add/update tests for changed scenarios and essential preserved invariants. Use `S1`, `S2` ... and link existing regression tests in specs with `[test: repo/relative/path]`; mark absent coverage `[untested]`, never invent test paths or successes.
6. Reinspect the **actual diff**, search for unexpected consumers and side effects, and update index/dependencies/source/test links as needed. Follow `flow-verify` for semantic review.

Finish with the affected flow IDs, intended doc changes, preserved rules, tests executed vs not executed, and any unrelated problems found. Avoid unrelated fixes and documentation churn.
