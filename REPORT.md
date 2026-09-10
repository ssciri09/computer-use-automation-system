# Design write-up

This repository implements one end-to-end vertical slice: an LLM discovers a
fee-waiver workflow against a deliberately hostile local banking UI, compiles a
reviewable capability artifact, and a model-free engine replays it with typed
inputs, outputs, runtime outcomes, safety checks, evidence, and attended human
handoff. Detailed diagrams are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## 1. Architecture

The system separates expensive, judgment-based discovery from cheap,
deterministic replay:

`goal → LLM discovery → trace + declaration → artifact → approval → replay`

`Cua.Core` owns the artifact/result contracts, policy, redaction, evidence,
HITL primitives, and `ISurface`. `Cua.Engine` owns discovery, compilation, and
replay without depending on Playwright or UIA. `Cua.Web` and `Cua.Desktop`
implement the surface seam; `Cua.Hosting` selects one. `Cua.Cli` and `Cua.Mcp`
are entry points.

The optional MCP interface turns each highest-version approved binding into a
discoverable tool with a generated JSON input schema. `tools/call` returns the
same replay result contract; irreversible tools additionally require explicit
per-call `ack_risk=true`. A committed invocation demonstrates discovery,
policy refusal, acknowledged execution, and structured output.

The model never receives a surface handle. During discovery it proposes typed
actions; the engine resolves the target, applies policy, executes, and records
ground truth. Replay applies allowlist/action policy before every navigation
and action, including runtime risk upgrades based on resolved control text.

The implementation is a single-process assignment slice, not deployment
infrastructure. One run owns one live surface. Storage, operator presentation,
and protocol transport remain behind replaceable seams.

## 2. Artifact schema

`CapabilityArtifact` is the contract between discovery and replay. It contains:

- capability identity, independent versions per application binding, vendor,
  tenant seam, surface kind, entry point, allowlist, and allowed actions;
- typed inputs/outputs, sensitivity metadata, and credential references;
- ordered steps with frame/pane paths, ranked locator chains, waits, risk, and
  extraction rules;
- declared success, business-outcome, recoverable, and escalation conditions;
- a final checkpoint, redaction rules, provenance, and approval state.

The compiler combines a mechanical trace with a semantic declaration. Target
metadata and executed actions come from the trace; outcome meaning comes from
the declaration. The raw model transcript remains evidence and cannot become
executable replay instructions.

Locators are ranked rather than singular. Modern web prefers published hooks
and accessible names; legacy web uses id/name/scoped text with coordinates
last; desktop maps the same vocabulary to AutomationId, Name, and pane-relative
coordinates. Fallback rank is logged as a drift signal.

`ArtifactValidator` rejects unsupported schema versions, duplicate IDs,
missing action data, empty locators, malformed regexes, dangling resume
targets, invalid retries, disallowed step actions, and contradictory output
values. Validation runs after compilation and before approval or replay.

## 3. Determinism & error handling

Replay performs, in order: policy check, resolve, conservative risk handling,
act, wait, evaluate declared conditions, extract outputs, and verify the final
checkpoint. No model is invoked.

The result contract separates:

- `success`: checkpoint verified and required outputs present;
- `business_outcome`: a legitimate answer such as account not found/closed;
- `escalation_pending`: operator context was queued but not resolved;
- `hard_failure`: an unknown or unsafe state with step, expected, observed, and
  redacted evidence.

Recoverable conditions use declared bounded backoff. A transient after an
effective irreversible action never retries because the write may have posted;
it escalates and checkpoint verification determines state after handback.
Session-expiry guards rerun the recorded auth phase once. Guards are checked
before main steps and while outcome assertions are polled.

Operational browser/UIA exceptions become structured hard failures rather than
escaping the replay contract. Artifact/input validation errors fail before the
surface is driven.

Discovery probes supplied synthetic variants after the successful flow so
error text is observed rather than invented. The capability declares
not-found, closed-account, host-congestion, security-interception, and success
states. Unknown states remain hard failures; open-ended LLM recovery during
production replay is intentionally excluded.

## 4. Heterogeneity & multi-tenant

`ISurface` is the seam between perception/action and the recorded flow. It
speaks observations, frame paths, locator chains, conditions, waits, and
opaque resolved targets. Replay does not branch on DOM, browser, or desktop
concepts.

The required concrete surface is legacy web through Playwright, including
named nested late-created frames, generated IDs, non-semantic controls, table
layouts, and slow host calls. As additional proof, the same engine supports a
semantic modern-web binding and WinForms UIA3 binding. Unnamed-frame,
bitmap/OCR, and Citrix-style surfaces are not claimed.

The reusable unit is `(vendor product, capability, app_binding, version)`, not
a tenant recording. Tenant onboarding starts with supervised preflight against
the compatible base artifact. Minor differences resolve through locator
fallbacks; meaningful differences become approved sparse overlays:

`vendor base → vendor-version overlay → surface/config overlay → tenant override`

Repeated tenant overrides should be promoted into a shared version overlay.
Fallback frequency and hard failures are drift signals; major vendor changes
create a new reviewed base version. Tenant routing, overlay resolution, and
fleet storage are design-only, as permitted by the assignment.

## 5. Escalation & handoff

Declared escalation conditions, discovery dead ends, and unapproved
irreversible actions create an `InterventionRequest` with capability, goal,
step, reason, redacted state, and resume plan.

Attended mode is the real handoff: `SessionControl` transfers an enforced
holder token from automation to human; the operator uses the same headed
browser and cookies; trusted click/change events are recorded with secret
values masked; control returns in `finally`; replay resumes at the declared
step and independently verifies the checkpoint. Surface methods reject
automation actions while the human owns the token.

Console handback uses Enter; the signal-file channel offers the same control
model to an external/mock operator coordinator without process stdin. The
committed attended run records masked PIN entry and authorization, then
completes with `human_assisted=true` after checkpoint verification.

The file queue is deliberately only an unattended notification mock. It
persists redacted context and returns `escalation_pending`; it does not retain
the live process after disposal. A real asynchronous operator service would
own the surface lifetime behind `IOperatorChannel`. The required same-session
mechanism is demonstrated by attended web mode.

## 6. Safety

Safety is enforced at execution boundaries:

- discovery and replay check explicit host/route or desktop-identity
  allowlists and allowed action types;
- resolved control text can upgrade declared risk but never downgrade it;
- irreversible actions require an approved artifact plus caller
  acknowledgement, otherwise a human confirms;
- ambiguous irreversible writes never auto-retry;
- artifacts contain credential references, never credential values;
- credential literals, sensitive inputs, tokens, SSNs, and account-shaped
  values are redacted at persistence boundaries;
- redaction fails closed on regex timeout.

Raw screenshots are disabled by default because pixels cannot be safely
pattern-redacted. Redacted structured observations provide failure evidence.
`--synthetic-evidence` permits screenshots only for this repository's
fabricated local target. Discovery should use sandbox data because the model
necessarily observes the live screen.

## 7. Cuts

- The operator UI is a terminal; the queue is notification-only.
- Desktop human-action recording is not implemented; attended web is the
  assignment handoff path.
- Tenant overlay resolution, routing, centralized drift scoring, and fleet
  infrastructure are design-only.
- Replay does not use open-ended LLM recovery.
- Vision/OCR and inaccessible remote-desktop surfaces are not implemented.
- Automated Playwright/UIA integration in CI is the next test investment.

These cuts preserve a thin but real version of every must-have while keeping
depth in the artifact contract, replay/error semantics, policy, and handoff.
