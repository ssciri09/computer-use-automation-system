# CUA — Computer-Use Automation System

This repository is a focused end-to-end implementation of record-once,
replay-many computer-use automation for legacy banking software:

```text
natural-language goal
  -> LLM observe / decide / act discovery
  -> typed, reviewable capability artifact
  -> approved deterministic replay (no LLM)
  -> success | business outcome | escalation | hard failure
```

The concrete target is a local, deliberately hostile legacy web application:
framesets, nested frames, generated IDs, non-semantic controls, host latency,
runtime business errors, and a security modal injected outside the work frame.
All people, accounts, credentials, and transactions in it are fabricated.

The design write-up is in [REPORT.md](REPORT.md). Detailed visual diagrams are
in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Fresh run evidence is indexed
in [evidence/README.md](evidence/README.md).

## Repository layout

- `src/Cua.Core` — artifact and result contracts, surface seam, policy,
  redaction, evidence, and intervention primitives.
- `src/Cua.Engine` — the LLM discovery loop, artifact compiler, validator, and
  deterministic replay engine.
- `src/Cua.Web` — Playwright web-surface adapter, including nested frames,
  locator chains, waits, screenshots, and attended human-action recording.
- `src/Cua.Desktop` — Windows UI Automation adapter demonstrating the same
  surface contract for native applications.
- `src/Cua.Cli` — discover, approve, replay, and catalog commands.
- `src/Cua.Mcp` — an optional agent-facing catalog for approved capabilities.
- `mock-legacy-bank` — local legacy target used by the demonstrated slice.
- `capabilities` — typed, versioned capability artifacts.
- `evidence` — redacted discovery and replay records.
- `tests/Cua.Tests` — unit and integration-style tests for load-bearing logic.

## Setup and run

Prerequisites:

- .NET 9 SDK
- Python 3.8+
- an Anthropic API key for discovery only

Create `.env` from the safe template:

```powershell
Copy-Item .env.example .env
```

Set these values in `.env`:

```dotenv
ANTHROPIC_API_KEY=your-key
CUA_MODEL=your-enabled-anthropic-model
CUA_USERNAME=operator
CUA_PASSWORD=letmein
```

`CUA_MODEL` is optional if the configured default is enabled for your account.
Do not commit `.env`; it is ignored by Git.

Build, test, and install Playwright Chromium:

```powershell
dotnet build Cua.sln
dotnet test Cua.sln
dotnet run --project src/Cua.Cli -- install-browsers
```

Start the target in a second terminal:

```powershell
python mock-legacy-bank/server.py
```

It listens at `http://127.0.0.1:8080/`.

## Demo path

### 1. Run genuine LLM discovery

```powershell
dotnet run --project src/Cua.Cli -- discover `
  --goal "In Fee Management, look up the account and waive the assessed overdraft fee, reaching the reversal confirmation. Record a reusable capability. Use 12345 for the successful recorded flow, then probe 55555 for transient congestion, 99999 for a closed-account rejection, 77777 for no records, and 00000 for a manager-override modal; probe the blocking modal last." `
  --id firstcore.fee_waiver `
  --url http://127.0.0.1:8080/ `
  --kind legacy_web `
  --binding firstcore-legacy `
  --vendor "FirstCore Banking Platform" `
  --param account_id=12345 `
  --headed `
  --synthetic-evidence
```

This is a real model-driven observe/decide/act run against the live local UI.
Probe actions ground exceptional-state declarations but are excluded from the
compiled replay prefix. `--synthetic-evidence` permits raw screenshots only
because this target contains fabricated data; omit it for real systems.

The fresh run produced:

- `capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json`
- `evidence/discovery-20260910-035609-912-8c7a584c5ffe4a5b8520bb9ac09c15cd/`

### 2. Review and approve

Read the generated JSON, then explicitly approve it:

```powershell
dotnet run --project src/Cua.Cli -- approve `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json
```

Approval is required before an artifact containing an irreversible action can
run unattended. Replay also requires explicit `--ack-risk`.

### 3. Deterministic replay

Success with extracted typed outputs:

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=12345 `
  --ack-risk
```

Expected business outcome (`not_found`, not a crash):

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=77777 `
  --ack-risk
```

Recoverable host congestion (declared backoff/retry, then success):

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=55555 `
  --ack-risk
```

Closed-account business outcome:

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=99999 `
  --ack-risk
```

No replay command invokes the LLM.

### 4. Human escalation and control transfer

Attended mode keeps the same headed browser session open. When the manager
override appears, the operator enters mock PIN `7391`, clicks **Authorize**,
then presses Enter in the terminal. Automation records the human action,
reclaims control, resumes at the declared checkpoint, and verifies completion.

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=00000 `
  --ack-risk `
  --headed
```

For a coordinator without access to process stdin, `--operator signal` holds
the human control token until its advertised `.resume` file is created. This
is the mode used by the committed attended evidence run; it recorded the
operator's focus, masked PIN input, and Authorize click before handback:

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=00000 `
  --ack-risk `
  --headed `
  --operator signal
```

Queue mode demonstrates asynchronous routing:

```powershell
dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-legacy.v1.json `
  --input account_id=00000 `
  --ack-risk `
  --operator queue
```

The queue adapter saves the intervention request, redacted current-state
evidence, and resume intent, then returns `escalation_pending`. It does not
claim to preserve a live browser after this local CLI process exits; a
production remote-session broker is explicitly a next step.

### 5. Replay the same capability on other surfaces

Modern web uses the same typed business contract with semantic locators and no
frames:

```powershell
python -m http.server 8090 --directory mock-modern-web

dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-modern.v1.json `
  --input account_id=12345 `
  --ack-risk
```

Native desktop uses Windows UI Automation. Automation IDs and accessible names
replace browser selectors, while pane paths replace frame paths:

```powershell
dotnet build mock-legacy-desktop/FirstCore.Desktop.csproj

dotnet run --project src/Cua.Cli -- replay `
  --artifact capabilities/firstcore.fee_waiver.firstcore-desktop.v1.json `
  --input account_id=12345 `
  --ack-risk
```

These are separate surface bindings of `firstcore.fee_waiver`, not separate
tenant recordings. Both were validated with successful model-free replays.

## Agent-facing capability interface (optional stretch)

Approved artifacts are exposed as typed MCP tools. Start the modern target,
build the server, then run the demonstration client:

```powershell
python -m http.server 8090 --directory mock-modern-web

dotnet build src/Cua.Mcp/Cua.Mcp.csproj
python scripts/mcp_demo.py firstcore_fee_waiver__firstcore_modern 12345
```

The client performs `initialize`, `tools/list`, `resources/list`, and
`tools/call`. It first proves an irreversible invocation without
`ack_risk=true` is refused, then invokes the approved capability with typed
arguments and prints the structured result. Unknown and draft capabilities are
also refused rather than guessed.

Fresh redacted invocation evidence is in
`evidence/mcp-20260910-044023-302-fde85e678a6044b4908cf9bdc641b256/`;
the walkthrough is in `evidence/mcp-capability-interface/README.md`.

## Running without live services

Replay needs no model service. The test suite also needs no browser, API key,
or network and exercises outcome classification, retries, policy blocks,
redaction, artifact validation, and pause/resume semantics:

```powershell
dotnet test Cua.sln
```

The local target can run faster during development:

```powershell
$env:LATENCY_SCALE = "0.2"
python mock-legacy-bank/server.py
```

Use the normal latency when producing submission evidence.

## Safety and evidence notes

- Navigation and action types are checked against configurable allowlists in
  both discovery and replay.
- Effective runtime risk can only be upgraded, never downgraded by an artifact.
- Credentials remain environment references; resolved secrets are registered
  with the redactor.
- Sensitive inputs and extracted non-enum outputs are masked at the persistence
  boundary. Typed values are returned to the immediate caller.
- Raw screenshots are disabled by default. Failure observations and queued
  intervention context use the stricter document redaction tier.
- Evidence run IDs are unique, and hard failures report the failing step,
  expected state, observed state, and evidence path.
