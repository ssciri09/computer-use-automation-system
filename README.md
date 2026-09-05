# cua — Computer-Use Automation System

An LLM discovers how to operate a legacy bank back-office app once; what it
learns is compiled into a **typed, versioned capability artifact**; production
runs **replay the artifact deterministically with no model in the loop**, with
explicit handling for business outcomes, transient conditions, hard failures,
and human escalation on the live session.

```
goal (NL) ──► LLM discovery loop ──► capability artifact ──► deterministic replay ──► result contract
                (observe/decide/act,      (reviewable JSON,        (no LLM; locator chains,   (success | business
                 policy-gated, recorded)   draft → approved)        assertions, guards)         outcome | escalation
                                                                        │                       pending | hard failure)
                                                                        └──► human takeover of the live session when stuck
```

The design write-up is in [REPORT.md](REPORT.md). Example artifact, discovery
and replay logs are in [evidence/](evidence/).

## Layout

| Path | What |
|---|---|
| `src/Cua.Core` | Surface seam, artifact schema, result contract, policy gate, redaction, evidence, HITL primitives. No dependencies. |
| `src/Cua.Web` | Playwright implementation of the surface seam (frames, locator chains, waits, human-action recorder). |
| `src/Cua.Engine` | Discovery agent (Anthropic API loop + artifact compiler) and the deterministic replay engine. |
| `src/Cua.Cli` | `cua` command line. |
| `tests/Cua.Tests` | Unit tests for the load-bearing logic (outcome taxonomy, retries, escalation/resume, guards, policy, redaction, compiler). |
| `mock-legacy-desktop/` | The desktop target: a WinForms thick-client build of the same fee-waiver workflow (late-built module pane, busy indicator removed from the UIA tree, security overlay on the root window outside the module pane). Proves one replay engine drives two surfaces. |
| `mock-legacy-bank/` | The target: a deliberately hostile mock "FirstCore Banking Platform" (framesets, nested late-injected iframes, `ext-genNN` ids, `<span onclick>` controls, multi-second host latency, security modal that escapes the module frame). Python 3.8+, no dependencies. |
| `capabilities/` | The capability catalog (recorded artifacts). |
| `evidence/` | Run evidence: JSONL logs, screenshots, model transcript, results. |

## Setup

Prereqs: .NET 9 SDK, Python 3.8+ (for the mock app), an Anthropic API key
(only for discovery — replay runs without any model access).

```bash
cp .env.example .env      # fill in ANTHROPIC_API_KEY; CUA_USERNAME/CUA_PASSWORD stay operator/letmein
dotnet build
dotnet run --project src/Cua.Cli -- install-browsers   # one-time Playwright Chromium install
```

Start the target app (keep it running in a second terminal):

```bash
python mock-legacy-bank/server.py
```

Run the tests:

```bash
dotnet test
```

## Demo path

Environment for every command below: `ANTHROPIC_API_KEY` (discovery only),
`CUA_USERNAME=operator`, `CUA_PASSWORD=letmein`.

**1. Discovery — the LLM records the capability (one real model run):**

```bash
dotnet run --project src/Cua.Cli -- discover \
  --goal "In Fee Management, look up the account and waive the assessed overdraft fee, reaching the reversal confirmation. Record the capability so an agent can waive a fee on any account. Test accounts you may probe to ground outcome conditions: 12345 healthy (use for the recorded flow), 55555 transient host congestion on first attempts, 99999 closed account (writes rejected), 77777 no records, 00000 restricted party whose waive triggers a blocking security modal - probe it last." \
  --id firstcore.fee_waiver \
  --url http://127.0.0.1:8080/ \
  --vendor "FirstCore Banking Platform" \
  --param account_id=12345 \
  --headed
```

The goal hands the model a tester's account matrix on purpose: after completing
the recorded flow it **probes** the variant accounts (probe actions are excluded
from the compiled flow) so every declared outcome condition is grounded in text
it actually observed — see REPORT.md §3 for the failure mode this prevents.

This emits `capabilities/firstcore.fee_waiver.v1.json` (a **draft**) and full
evidence under `evidence/discovery-*/` (JSONL log, redacted model transcript,
screenshots, the model's declaration).

**2. Review & approve** (a human reads the artifact, then):

```bash
dotnet run --project src/Cua.Cli -- approve --artifact capabilities/firstcore.fee_waiver.v1.json
```

**3. Deterministic replay — the production path, no LLM:**

```bash
# happy path: fee waived, confirmation extracted
dotnet run --project src/Cua.Cli -- replay --artifact capabilities/firstcore.fee_waiver.v1.json \
  --input account_id=12345 --ack-risk

# expected business outcome, not a crash: closed account → outcome=not_permitted
dotnet run --project src/Cua.Cli -- replay --artifact capabilities/firstcore.fee_waiver.v1.json \
  --input account_id=99999 --ack-risk

# transient host congestion → declared retry policy → success
dotnet run --project src/Cua.Cli -- replay --artifact capabilities/firstcore.fee_waiver.v1.json \
  --input account_id=55555 --ack-risk

# unknown account → outcome=not_found
dotnet run --project src/Cua.Cli -- replay --artifact capabilities/firstcore.fee_waiver.v1.json \
  --input account_id=77777 --ack-risk
```

**4. Human-in-the-loop — security interception on the live session:**

```bash
# attended: the security modal triggers an escalation; YOU take over the live
# browser window (enter manager PIN 7391, click Authorize), then press ENTER
# in the terminal to hand control back; the run resumes and completes.
dotnet run --project src/Cua.Cli -- replay --artifact capabilities/firstcore.fee_waiver.v1.json \
  --input account_id=00000 --ack-risk --headed

# unattended: the same condition routes an intervention request (context,
# screenshot, resume plan) to the operator queue and parks the run as
# escalation_pending.
dotnet run --project src/Cua.Cli -- replay --artifact capabilities/firstcore.fee_waiver.v1.json \
  --input account_id=00000 --ack-risk --operator queue
```

**5. The same capability on a second surface (desktop):**

```bash
dotnet build mock-legacy-desktop/FirstCore.Desktop.csproj

# same engine, same artifact vocabulary, native UIA adapter - no browser
dotnet run --project src/Cua.Cli -- replay   --artifact capabilities/firstcore.fee_waiver.firstcore-desktop.v1.json   --input account_id=12345 --ack-risk          # success + RVSL- confirmation
#  --input account_id=99999                    # business outcome: not_permitted
#  --input account_id=55555                    # transient -> retry -> success
#  --input account_id=00000 --operator queue   # escalation_pending
```

**6. The catalog an AI agent would call:**

```bash
dotnet run --project src/Cua.Cli -- list
```

## Running without live services

- **Replay never needs a model.** Only `discover` touches the Anthropic API.
- The target app is local and dependency-free.
- The unit tests exercise the replay engine's full semantics (retry, outcome
  taxonomy, escalation, resume, guards) against a scripted fake surface — no
  browser, no network: `dotnet test`.
- `LATENCY_SCALE=0.2 python mock-legacy-bank/server.py` makes the mock app 5×
  faster for iterating (leave it at 1.0 when producing evidence).

## Notes

- The mock app's credentials (`operator` / `letmein`) and all data are
  fabricated; nothing here touches a real system.
- Secrets never enter artifacts or logs: artifacts store a credentials *ref*
  (`env://CUA`), resolved values are registered with the redactor, and
  redaction is applied at the persistence boundary.
