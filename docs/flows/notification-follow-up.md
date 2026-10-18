# FDD-006 — Result delivery and conversational follow-up

Status: Draft

## Trigger / Outcome
- Initiator: task emits a notification-worthy result or authorized user asks a follow-up via an inbound channel.
- Outcome: result reaches permitted destination reliably, or follow-up continues relevant conversation and returns a new answer.

## Inputs
- Run result, notification destination/channel, delivery policy, conversation ID, inbound message and identity.

## Outputs
- Delivery record, message with result/source references, or a follow-up task/run referencing prior context.

## Flow
1. Convert run outcome to channel-neutral `OutgoingMessage` and persist to outbox.
2. Resolve only authorized destination(s); send with channel-specific adapter.
3. Retry temporary delivery failures while deduplicating message attempts as far as provider supports.
4. On incoming authorized natural-language follow-up, resolve conversation and load bounded prior context.
5. Submit new run through the common Task Runner and deliver outcome through the selected channel.

## Validations
- Sender/recipient allowlists, secrets/privacy policy, output size, channel-specific formatting and correlation IDs.

## Invariants
- Communication channel does not contain task business logic.
- Failed delivery does not rewrite an otherwise completed TaskRun.
- Adding a second channel should require a new adapter/registration, not a Task Runner rewrite.

## Failures / Side Effects
- Network/Telegram errors are retried through outbox; no duplicate task execution due to notification failures.
- Unauthorized senders cannot use the bot to control desktop.

## Dependencies
- None.

## Scenarios
- S1: Given a completed run and failing Telegram API, when retries occur, then run status remains completed and outbox tracks attempts. [untested]
- S2: Given the owner asks a follow-up referencing previous summary, when message is received, then contextual question is processed in that conversation. [untested]
- S3: Given an unrecognized Telegram sender, when commands are sent, then no tool execution starts. [untested]
- S4: Given a new channel implementation, when configured, then it can deliver without modifying Core orchestration. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§11–13; backlog PR-12, PR-13.

- Neutral history/session foundation (PA-020): `src/PersonalAgent.Core/Domain/ConversationModels.cs`, `src/PersonalAgent.Core/Persistence/IConversationRepository.cs` and `src/PersonalAgent.Persistence/SqliteConversationRepository.cs` store ordered messages and immutable provider/model/schema-tagged session checkpoints. `tests/PersonalAgent.IntegrationTests/PersistenceConversationTests.cs` verifies real SQLite reopen, schema/provider/model mismatch refusal, duplicate message constraints and cross-conversation run validation. This is storage scaffolding, not inbound routing, provider session resume, delivery or complete S1-S4 coverage; all remain `[untested]`.

## Unknowns
- Channel-specific rate limiting and external data protection rules require deployment verification.
