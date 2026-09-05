# Agent-facing capability interface (MCP)

The catalog exposed over MCP (JSON-RPC 2.0 on stdio) so an AI agent can
discover recorded capabilities by name, invoke them with typed arguments, and
reason about the result contract. Server: `src/Cua.Mcp` (`cua-mcp`). Client
used here: `scripts/mcp_demo.py`, which speaks the same protocol any agent's
MCP client speaks.

- `session-success.log` — full walkthrough ending in a successful invocation
- `session-business-outcome.log` — the same tool invoked on a closed account

## What the logs show

**Discovery.** `tools/list` returns one tool per *approved* capability, with
JSON Schema derived from the artifact's typed inputs (including the regex
pattern and a PII/redaction note) and a description naming the surface, the
declared outputs with their outcome enum, and the goal it was recorded from.
Three tools appear — the legacy web, modern web, and desktop bindings of the
same capability — which is the multi-surface story made callable.

**Review.** `resources/list` exposes every artifact, drafts included, as a
readable JSON resource. Callable and reviewable are deliberately different
sets: the draft legacy v1 is listed for review but is not a tool.

**Invocation.** `tools/call firstcore_fee_waiver__firstcore_modern
{"account_id": "12345"}` replays the recorded artifact against the live app
with no model in the loop and returns:

```
SUCCESS: account_name=M R HOLLOWAY, outcome=waived, confirmation_number=RVSL-A4DBF291
```

plus the full `ReplayResult` as structured content, including the evidence
directory for that run.

**The distinction that matters.** Invoked on account `99999`, the same tool
returns `status: business_outcome`, `outcome: not_permitted`, and — critically
— **`isError: false`**. The account being closed is an answer the calling
agent must act on, not a failure to retry. A hard failure would set
`isError: true`; an escalation returns `escalation_pending` with the run
parked for an operator rather than blocking the agent.

**Refusal.** An uncatalogued name is refused rather than guessed at. A
capability that exists only as a draft is refused the same way, naming its
approval state — only human-approved artifacts are callable, which is what
makes handing this surface to an agent defensible.
