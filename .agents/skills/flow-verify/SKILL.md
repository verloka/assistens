---
name: flow-verify
description: Audit code changes, PRs, or implemented features against Flow Driven Development (FDD) specs. Use after flow-develop, on review requests, or when checking whether end-to-end behavior, business invariants, dependent flows, and scenario test coverage remain intact. Analyze actual diffs, detect spec drift, report PASS/FAIL/UNKNOWN/NOT TESTED; do not silently edit approved specs.
---

# Verify changed flows

Default to a **read-only semantic review** unless the user asks for fixes. Active specs are binding; Draft specs are reference only.

1. Establish scope from the task and the **actual** staged/unstaged/branch diff. If absent, inspect explicitly requested paths/flows and state that scope.
2. Find affected flow IDs via index, changed entry points, state/events/schema, shared symbols, forward and reverse dependencies. Follow cross-service and async boundaries where relevant.
3. Compare the implementation with **each affected Active flow**, checking trigger, inputs/outputs, authorization, validation, invariants, alternate paths, failures, idempotency, state transitions and side effects. Check whether intentional contract changes were explicitly requested and documented; never change a spec just to silence a failing review.
4. Review numbered scenarios (`S1`, `S2`...), linked `[test: path]` files and `[untested]` gaps. Run feasible relevant tests; never treat reading a test file as executing it. Distinguish test findings from static inference.
5. Check documentation consistency directly: listed files exist, flow statuses and dependencies agree with the index, source/test references resolve, and required scenarios remain documented. This is complementary to the semantic and test review, not a substitute for it.
6. Report findings by severity with flow ID, rule/scenario, and precise code/test evidence. Label `PASS`, `FAIL`, `UNKNOWN` or `NOT TESTED`. Note uncovered scenarios, incomplete impact analysis and discrepancies outside task scope.

Do not add a separate report file, change the code or promote Draft to Active by default.
