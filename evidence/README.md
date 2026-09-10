# Fresh end-to-end evidence

All evidence below was generated on 2026-09-10 against the fabricated local
FirstCore target. The capability under test is
`capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json`.

## Genuine LLM discovery

`discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/`

- Real Anthropic-driven observe/decide/act run against the live legacy UI.
- 34 model turns and 21 recorded actions, including post-success probes.
- Contains the structured run log, redacted model transcript, model
  declaration, and synthetic-target screenshots.
- Compiled the reusable flow plus grounded `not_found`, `account_closed`,
  transient host-congestion, and manager-override conditions.

## Deterministic replay: success

`replay-20260910-041027-535-e8f0c90c9290487e81d9622e6c447024/`

- Status: `success`
- Checkpoint verified after the irreversible fee-waiver action.
- Returned `outcome=waived` plus confirmation, fee-reference, and amount
  fields to the caller. Raw extracted values are masked in persisted evidence.

## Deterministic replay: business outcome

`replay-20260910-041042-869-015469c479e84ffe844701f8fef16eaa/`

- Status: `business_outcome`
- Outcome: `not_found`
- Terminated deliberately at inquiry step `s9`; no waiver action was attempted.

## Deterministic replay: recoverable condition

`replay-20260910-041055-252-b99b39146e094a4e953ba82fc1740247/`

- Status: `success`
- Detected host code `HOST-0521` twice.
- Applied declared 2-second and 5-second backoffs, retried the same step, then
  continued only after the success assertion matched.

## Deterministic replay: escalation

`replay-20260910-041131-982-cfe51a0e932949a5ab74e4cd5cdf46db/`

- Status: `escalation_pending`
- Detected the top-frame manager-override security modal at step `s10`.
- Saved redacted context and resume intent in
  `operator-queue/intervention-replay-20260910-041131-982-cfe51a0e932949a5ab74e4cd5cdf46db-s10.json`.
- Queue mode is notification-only; the unattended CLI session is closed after
  routing. Attended mode is the implemented same-live-session takeover path.

## Attended handoff: resolved on the same live session

`replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a/`

- Status: `success`
- `human_assisted=true` with a resolved intervention record.
- Automation transferred its enforced control token to the operator and
  remained paused until an external handback signal was received.
- `human_actions.jsonl` records three actions from the live browser: focus the
  override field, masked PIN input, and the Authorize click.
- Replay reclaimed control, jumped to the declared checkpoint, and verified
  the reversal confirmation without invoking an LLM.

Every replay above used the saved approved artifact and made no LLM call.

## Additional surface binding: modern web

`replay-20260910-042141-875-077ad5c195294f548f31a7ef2fdf169e/`

- Artifact:
  `capabilities/firstcore.fee_waiver.firstcore-modern.v1.json`
- Status: `success`
- Used semantic `data-testid`/ARIA locator chains in a single-document web UI.
- Returned the shared waiver contract plus the modern surface's optional
  `account_name` output.

## Additional surface binding: native desktop

`replay-20260910-042259-024-2dbfccfb12cf4dd5a2467b4b0bb4b75a/`

- Artifact:
  `capabilities/firstcore.fee_waiver.firstcore-desktop.v1.json`
- Status: `success`
- Used Windows UI Automation IDs, accessible names, and pane paths.
- Verified the same checkpoint and returned the shared waiver outputs.

Both bindings replayed deterministically with no LLM call. Persisted business
outputs are masked while result status, timing, and evidence identity remain
reviewable.

## Agent-facing MCP invocation

`mcp-20260910-044023-302-fde85e678a6044b4908cf9bdc641b256/`

- An MCP client discovered all approved bindings through `tools/list`, including
  their typed input schemas.
- A call without `ack_risk=true` was refused because the capability contains an
  irreversible action.
- The acknowledged call invoked
  `firstcore_fee_waiver__firstcore_modern` and returned `success`.
- Full protocol walkthrough: `mcp-capability-interface/README.md`.
