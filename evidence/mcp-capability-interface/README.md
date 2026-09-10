# Agent-facing capability invocation

This evidence demonstrates the optional agent-facing capability interface.

The client at `scripts/mcp_demo.py` connected to the stdio MCP server and
performed the same sequence an AI agent uses:

1. `initialize`
2. `tools/list` — discovered three approved surface bindings with typed schemas
3. `resources/list` — discovered reviewable artifact resources
4. `tools/call` without `ack_risk` — correctly refused the irreversible action
5. `tools/call` with `account_id` and `ack_risk=true` — invoked the approved
   modern-web binding
6. an unknown capability call — refused instead of guessed

The successful invocation produced:

- `evidence/mcp-20260910-044023-302-fde85e678a6044b4908cf9bdc641b256/log.jsonl`
- `evidence/mcp-20260910-044023-302-fde85e678a6044b4908cf9bdc641b256/result.json`

The result status is `success`, the checkpoint was verified, and typed outputs
were returned to the calling client. Persisted copies redact the account and
extracted business values. The MCP server made no LLM call.

Only the highest approved version of each capability binding is callable.
Draft artifacts remain readable as resources but are refused by `tools/call`.
