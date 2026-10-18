# FDD source provenance

- Repository: https://github.com/verloka/fdd
- Pinned commit: `9f4e6e7bc4402e845894eeac623b534e797daa2f` (master, 2026-10-11)
- License: Apache License 2.0; full terms in `LICENSE`.
- Installation method: upstream documented **manual installation**, Codex only.
- Unmodified upstream files: three `.agents/skills/*/SKILL.md`, three `.agents/skills/*/agents/openai.yaml`, `docs/flows/_template.md`, and appended `AGENTS.md` FDD policy block.
- Project-specific files (new, not upstream): `docs/flows/index.md`, seven initial `Draft` flow contracts, and integrations within existing project instructions/readme.
- No migration support, legacy FDD naming, Python helper, or installer bundled.
- `flow-bootstrap` is for existing code; in this greenfield starter we only use proposed Draft flow documents to plan known product requirements. Revalidate them as the code is implemented.

## Integrity: upstream Git blob SHA-1

| File | SHA-1 |
| --- | --- |
| `flow-bootstrap/SKILL.md` | `d281ffc014c4875d18ff34488fb21512a2ef1eb7` |
| `flow-develop/SKILL.md` | `05ed669e87955a1f382c578a88998faef1e0ca19` |
| `flow-verify/SKILL.md` | `d55655fed84cb675e7dd934bacfe46eaf16e1b1c` |
| `docs/flows/_template.md` | `360e4b3a5918536a9798b6341f326d343ad20a55` |

Source FDD is included as vendored instruction files, not a runtime application dependency.
