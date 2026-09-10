# Evidence reviewer guide

This folder contains fresh evidence generated on 2026-09-10 against the
fabricated local FirstCore banking target. No real customer data is present.

The long directory names are immutable run IDs. They connect every result,
event, intervention, and screenshot to one execution. Follow the short path
below; the later sections are supporting proof.

## Five-minute required review

### 1. Review the saved capability

Open [example-artifact.json](example-artifact.json).

This is a byte-for-byte snapshot of the approved canonical artifact at
`/capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json`. Focus on:

- typed `account_id` input and declared outputs;
- ranked locator candidates and nested frame paths;
- `success`, `business_outcome`, `recoverable`, and `escalate` assertions;
- irreversible risk classification and final checkpoint;
- approval, provenance, redaction, vendor, tenant, and surface metadata.

### 2. Confirm genuine LLM discovery

Run:
[`discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/`](discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/)

Open in this order:

1. [log.jsonl](discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/log.jsonl)
   — 34 model turns and 21 executed actions.
2. [transcript.jsonl](discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/transcript.jsonl)
   — redacted model/tool conversation.
3. [declaration.json](discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/declaration.json)
   — semantic contract emitted separately from the mechanical trace.
4. [initial screenshot](discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/01-initial.png)
   and [manager-interception screenshot](discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/14-s21-click.png)
   — richer visual evidence from the synthetic target.

The model completed the successful flow, then probed fabricated variants to
ground the not-found, closed-account, congestion, and override conditions.
Probe actions were not compiled into deterministic replay.

### 3. Confirm deterministic success

Run:
[`replay-20260910-041027-535-e8f0c90c9290487e81d9622e6c447024/`](replay-20260910-041027-535-e8f0c90c9290487e81d9622e6c447024/)

- [result.json](replay-20260910-041027-535-e8f0c90c9290487e81d9622e6c447024/result.json)
  reports `success` and `outcome=waived`.
- [log.jsonl](replay-20260910-041027-535-e8f0c90c9290487e81d9622e6c447024/log.jsonl)
  shows policy checks, waits, assertions, extraction, and checkpoint success.
- No replay event invokes an LLM.

### 4. Confirm exceptional-state handling

Expected business answer:

- [not-found result](replay-20260910-041042-869-015469c479e84ffe844701f8fef16eaa/result.json)
  reports `business_outcome`, not failure.
- Its [event log](replay-20260910-041042-869-015469c479e84ffe844701f8fef16eaa/log.jsonl)
  stops at inquiry step `s9`; no waiver is attempted.

Recoverable runtime condition:

- [retry result](replay-20260910-041055-252-b99b39146e094a4e953ba82fc1740247/result.json)
  ends in `success`.
- Its [event log](replay-20260910-041055-252-b99b39146e094a4e953ba82fc1740247/log.jsonl)
  records two `HOST-0521` matches and bounded 2-second/5-second backoffs.

### 5. Confirm real human handoff and resume

Run:
[`replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a/`](replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a/)

Open:

1. [result.json](replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a/result.json)
   — `success`, resolved intervention, and `human_assisted=true`.
2. [human_actions.jsonl](replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a/human_actions.jsonl)
   — focus, masked PIN input, and Authorize click recorded while the human held
   the same live browser session.
3. [log.jsonl](replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a/log.jsonl)
   — intervention, handback, resume jump, and independent checkpoint.
4. [operator request](operator-signals/intervention-replay-20260910-043100-571-8f775a8f05244fc7aa17645a0cfda45a-s10.json)
   — redacted current state, reason, and resume plan supplied to the operator.

## Supporting robustness evidence

### Unattended escalation

[Result](replay-20260910-041131-982-cfe51a0e932949a5ab74e4cd5cdf46db/result.json)
reports `escalation_pending`. The
[queued request](operator-queue/intervention-replay-20260910-041131-982-cfe51a0e932949a5ab74e4cd5cdf46db-s10.json)
contains redacted context and resume intent. Queue mode is deliberately
notification-only; attended mode above proves live-session handoff.

### Heterogeneous surfaces

- [Modern-web result](replay-20260910-042141-875-077ad5c195294f548f31a7ef2fdf169e/result.json)
  — same capability contract through semantic `data-testid`/ARIA locators.
- [Native-desktop result](replay-20260910-042259-024-2dbfccfb12cf4dd5a2467b4b0bb4b75a/result.json)
  — same engine through Windows UI Automation IDs, names, and pane paths.

Both are successful model-free replays.

## Optional stretch evidence

### Agent-facing MCP invocation

Open [the MCP walkthrough](mcp-capability-interface/README.md), then inspect:

- [MCP result](mcp-20260910-044023-302-fde85e678a6044b4908cf9bdc641b256/result.json)
- [MCP replay log](mcp-20260910-044023-302-fde85e678a6044b4908cf9bdc641b256/log.jsonl)

The client discovered three approved typed tools, proved that an irreversible
call without `ack_risk=true` is refused, then invoked the modern binding and
received the normal structured success contract.

## Reading persisted values

Immediate callers receive typed business outputs. Persisted copies mask
sensitive inputs, credentials, names, account identifiers, confirmation
references, and extracted values. Raw screenshots are disabled by default and
were enabled only for the explicitly fabricated discovery target.
