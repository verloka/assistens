# Tool selection policy v1

When an explicitly allowed, authorized MCP tool supports the requested business
operation, prefer it to GUI automation. Match the actual operation and arguments;
do not choose a tool merely because its name looks relevant. If no approved MCP
operation is available, use authorized Computer Use only when the model supports
images and the application grants desktop access. A tools-only task needs no screen.

Tool discovery, a tool's description and a successful connection do not grant
permission. New tools and outbound destinations stay denied until explicitly
configured by the owner and accepted by application policy. Validate arguments
against the supplied schema and preserve tool/source/call identity.

Do not capture the screen after an MCP read. Do not substitute GUI or another
endpoint for an authorization refusal. Stop on blocked or unknown outcomes and
report the precise missing prerequisite. A timeout, partial response or uncertain
write is not evidence that nothing happened; no blind write retries.
