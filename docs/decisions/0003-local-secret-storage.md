# Local secret storage boundary

Date: 2026-10-18. Status: planned decision, implementation deferred to PA-116.

Use Windows per-user protected storage (DPAPI or Windows Credential Manager) in
Persistence. Domain records and exported settings will hold references, not raw
provider/MCP/channel credentials. Never commit tokens, screenshot captures or
corporate message bodies. API access is not implied by a ChatGPT/Claude subscription.

There is currently no credential acquisition/storage or live provider adapter.
Select and verify the concrete Windows API in PA-116, including restart retrieval
and export/log redaction. No secret-protection acceptance is claimed by this ADR.

References: Development Plan §§13.2, 14, 16 Phase 0; PA-116/PA-117/PA-120.
