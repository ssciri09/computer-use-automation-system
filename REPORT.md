# Design write-up

## 1. Architecture

Four assemblies with one-way, compiler-enforced dependencies, arranged around
two seams:

```
Cua.Cli ──► Cua.Engine ─────────────┐
   │            │                   ▼
   ├──► Cua.Web ┼────────────► Cua.Core   (schema, contracts, policy,
   └──► Cua.Desktop ──────────►    │       redaction, HITL, ISurface)
```

`Cua.Core` is dependency-free and owns everything both sides share: the
artifact schema, the result contract, the policy gate, the redactor, the HITL
primitives, and `ISurface` — the perception/action seam. `Cua.Web`
(Playwright) and `Cua.Desktop` (UIA3) are the only assemblies that know a
concrete surface exists. `Cua.Engine` holds the two execution paths —
LLM discovery and deterministic replay — which share nothing but Core, so
replay cannot grow a model dependency.

Key decisions:

- **Manual model loop, not an agent framework.** Every model-proposed action
  passes the `PolicyGate` *between decision and execution*. The model never
  holds a handle to the surface; it emits intents, the engine validates and
  executes. That gate placement is the safety architecture.
- **Mechanical trace + declared semantics.** Discovery records ground truth
  for every executed action (resolved element id/name/text/position, frame).
  The model separately *declares* the semantic layer (`declare_capability`):
  outcome classification, checkpoint, outputs, auth steps. The compiler merges
  the two — steps and locators are facts, judgment is captured only where
  judgment is needed, and all of it is reviewable before approval.
- **Single process, no queues.** The scale unit is one run; the complexity
  budget went to the artifact and replay semantics. The operator channel and
  artifact store are interfaces a production deployment re-implements without
  touching the engine.
- **Observation is a compact digest, not screenshots**: per frame, the
  fields/controls/messages/tables with ids and text (~2–4 KB). On a hostile
  DOM this is more token-frugal and more actionable than pixels, and the same
  digest shape is produced from the UIA tree (§4). Screenshots are still
  captured as evidence and escalation context.

## 2. Artifact schema

An artifact is a **capability contract**, not a macro: identity + versioning,
vendor and tenant blocks, surface (kind, entry, allowlist, app binding), typed
`inputs` (sensitivity + redaction flags), typed `outputs` (enums for outcome
codes), a credentials *reference* (never values), `guards`, ordered `steps`,
redaction spec, and provenance (model, run id, `draft`/`approved`).

- **Frame paths are part of every locator** (`["wrkFrm","modFrm"]`). On
  frameset-era apps the frame is half the address; ambient frame state is how
  replays assert against the wrong document. Conditions can override the frame
  per assertion — the security modal is asserted in the *top* document because
  that is where the app injects it.
- **Locators are ranked chains** — id → name attribute → scoped visible text →
  frame-relative coordinates — built from resolution-time metadata, never
  guessed, with the robustness reasoning recorded. Replay logs every fallback
  hit; that signal is the drift detector (§4).
- **Assertions are declared per step in evaluation order**, classified
  `recoverable` (with retry policy), `business_outcome` (emits outputs,
  terminal), `escalate` (reason + resume-at), or `success`. There is
  deliberately **no `hard_failure` class**: a hard failure is the *absence* of
  any recognized condition at timeout, never a pattern match — so conflating
  outcomes with failures cannot be expressed.
- **Waits attach to the step that causes them** (`wait_after`: "busy spinner
  must disappear") — sequencing on slow host calls is a property of the
  action, not a free-standing step.
- **Parameterization is mechanical.** Typed values matching a declared run
  parameter compile to `value_ref: inputs.account_id`; credential placeholders
  compile to refs against `env://`/`vault://`. No concrete member id or secret
  ever enters an artifact.

## 3. Determinism & error handling

Replay executes steps in order with zero model involvement: resolve the
locator chain (bounded polling absorbs late-injected iframes), act, run the
attached wait, then poll the declared assertions in order until one matches or
the timeout expires. The result contract is a hard four-way split: `success`
(checkpoint verified, outputs returned), `business_outcome` (the app
legitimately said no — an answer, not a crash), `escalation_pending`
(intervention raised unattended, context preserved), `hard_failure` (step id,
expected vs observed, screenshot, observation dump).

Runtime conditions — the interesting failures, since the UI is stable:

- **Transients** (host-congestion banner): `recoverable` → re-execute with
  declared backoff, bounded attempts; exhaustion is a hard failure, never a
  silent loop. One rule is engine-enforced regardless of the artifact: a
  transient on an **irreversible** step never retries — the write may already
  have posted — it escalates to a human and the checkpoint re-verifies after
  handback.
- **Expected rejections** ("no records found", "account is closed"):
  terminal `business_outcome` with an outcome code the calling agent acts on.
- **Unexpected dialogs / session expiry**: artifact-level `guards` checked
  before every step. The compiler mechanically emits a session-expiry guard
  (the recorded sign-on field reappearing ⇒ rerun the `auth`-phase steps once,
  resume); escalation guards hand off to a human.
- **Unknown states**: hard failure with expected-vs-observed and full
  evidence — debuggable without rerunning.

**Grounding declared conditions — a failure we found and fixed.** The first
live discovery run declared outcome conditions for states the model never
observed, with invented text (`"HOST BUSY"` where the app says `HOST-0521:
Legacy host link congested`). Those replays would hard-fail instead of
classify — a hallucinated condition is worse than an undeclared one because it
looks reviewable. The fix is systemic: discovery actions take a `probe` flag
(exploratory, excluded from the compiled flow — enforced as a contiguous-prefix
rule in the compiler), and the agent must ground every declared string in
observed text by probing variant inputs after the main flow. Run 1 is kept in
`/evidence/` as the failure exhibit; the shipped artifact is the probe-grounded
run 2, whose review still caught one residual (a confirmation-id regex
generalized from a single example, `RVSL-\d+`, silently truncating hex ids —
corrected, and vindicated when the next replay drew `RVSL-4CB4F665`). The
`draft → approved` gate exists for exactly this class of defect.

UI drift (secondary here): locator chains absorb id churn, fallback hits are
logged as the drift signal, and approval + versioning make re-recording a
controlled event.

## 4. Heterogeneity & multi-tenant

**Surface abstraction — implemented both sides of the seam.** Everything
above `ISurface` speaks frame paths, locator chains, conditions, and
observations. The Playwright adapter handles (legacy) web — the mock is
deliberately hostile: framesets, generated ids, `<span onclick>` controls. The
UIA3 desktop adapter consumes the *same artifact vocabulary* via an explicit
mapping: frame path → window/pane path, css `#id` → AutomationId, name/text →
Name, coordinates stay scope-relative; xpath is dropped by design. The
artifact's `surface.kind` selects the adapter at composition time; the engine,
compiler, and schema never branch on it. Coordinates-as-last-resort exists in
the chain because it is the one rank that survives bitmap-only surfaces.

This is validated, not asserted: `mock-legacy-desktop/` is a WinForms build of
the same fee-waiver workflow with the same obstacles rendered natively — a
module pane that does not exist until ~1.2s after the nav click, a busy
indicator removed from the tree rather than hidden, and a security overlay
painted on the **root window, outside the module pane**. The same replay
engine drives it through all five outcome classes (see
`evidence/desktop-*`): success with an extracted `RVSL-…` confirmation,
`not_permitted`, `not_found`, transient congestion retried twice then
succeeding, and escalation raised because the assertion names the root frame.
Running it was worth more than reasoning about it — it exposed three real
defects (unresolved relative entry paths, a risk-confirmation that left its
intervention record `pending` on a successful run, and a leaked app process
whose stale window silently swallowed clicks). Still cut: human-action
recording during a desktop takeover is a no-op (§7).

**Multi-tenant reuse.** The unit of reuse is the vendor-product artifact, not
the tenant recording. The artifact carries `vendor` (product + version range)
and `tenant` (recorded-for + sparse per-step override map). Rollout: replay
the base artifact against a new tenant supervised; fallback hits and hard
failures identify *which steps* need overrides — recorded as overrides, never
a fork. A tenant whose replays increasingly resolve on fallback ranks gets
flagged for re-validation before it breaks; vendor version bumps trigger one
central re-recording, not per-tenant rebuilds.

**One tenant, several surfaces.** The same capability can exist on the web
portal *and* the thick client, so artifacts carry an `app_binding` and version
lines count per capability + binding (a desktop recording is a sibling, never
"v3" of the web one). Callers ask for the capability; a per-tenant policy
picks the binding deterministically — pin, then approval state, then
stability history. Reads may fail over between sibling bindings; writes never
auto-fail-over past the irreversible step (the waive may have posted) — that
escalates.

## 5. Escalation & handoff

**Detecting "stuck":** declared `escalate` assertions (the security modal,
asserted in the top frame), guards for unexpected states, the discovery
agent's own `escalate_to_human` tool, and irreversible steps lacking standing
authorization (§6) — all routed through one mechanism.

**Routing with context:** an `InterventionRequest` carries capability + goal,
current step, reason, screenshot, redacted observation digest, and the resume
plan. Attended mode presents it at the operator terminal; unattended mode
persists it to a queue and the run ends `escalation_pending` with evidence
preserved.

**Control transfer is explicit and enforced.** `SessionControl` is a
holder-token state machine (`Automation` ⇄ `Human`); while a human holds
control the engine cannot drive the surface (asserted on every action). The
recorder — an init script in every frame reporting only trusted events —
buffers the human's clicks and edits, password/PIN values masked. It is the
**same live browser session** (same cookies, same in-page state — the override
modal exists only there). On handback the human's actions go to evidence and
the run resumes at the declared `resume_at` step, typically the checkpoint,
which **re-verifies state rather than trusting that the human finished**.

**Mocked:** only the operator *presentation* (terminal + queue directory).
The seam (`IOperatorChannel`), control-transfer model, context payload, action
recording, and resume semantics are real and unit-tested.

## 6. Safety

- **Allowlist** checked on every navigation *and* on the current context
  before every action, discovery and replay alike. Entries can be
  route-scoped (`host:port/portal`); bare hosts allow all routes; desktop
  tokens are process/app identities. Refusals are visible in the transcript
  (`POLICY_BLOCKED`) as evidence.
- **Risky actions, layered:** declared risk per step, *upgraded* (never
  downgraded) by target-text patterns when the model under-declares;
  `flag`/`confirm`/`block` modes; on replay, irreversible steps run unattended
  only with an **approved** artifact *and* `--ack-risk`, otherwise they become
  human confirmations; drafts refuse to replay; transients on irreversible
  steps never auto-retry (§3); failover never crosses the irreversible
  boundary (§4).
- **Secrets and PII:** artifacts store credential references; resolved values
  are registered with the redactor as literals; the model sees only
  placeholders. Redaction applies at the persistence boundary — secret
  literals, key/token/SSN patterns, inputs marked `redact_in_logs`,
  confirmation-id patterns (outputs go to the caller, not logs). Persisted
  screen captures (failure dumps, escalation digests) get a stricter document
  tier that also masks account-shaped digit runs, protecting *bystander*
  customers visible on shared screens.
- **Worked example — one click through every layer.** The discovery model
  under-declares the "Waive Fee" click as `risk: safe`; the gate's text
  patterns upgrade it to `irreversible` (evidence: `risk_flagged` in the run
  log). At replay the step runs unattended only because a human approved the
  artifact *and* the caller passed `--ack-risk` — remove either and it pauses
  for human confirmation. The click fires and the screen shows only a
  HOST-0521 congestion banner: did the waive post? Unknown — so the engine
  refuses the retry (`retry_refused`) and escalates instead of risking a
  double-post. Had the model tried `navigate` to a vendor-docs site first,
  the transcript would show `POLICY_BLOCKED: host … not in the allowlist`.
  And when a failure dump captures the fee register, bystander account
  `40219` is persisted as `▮▮REDACTED▮▮` while the caller still receives the
  real `RVSL-…` confirmation — outputs go to the caller, not the logs.
- **Limits:** discovery necessarily shows on-screen data to the model, so it
  runs against test/sandbox data; replay sends nothing anywhere. Redaction is
  pattern+literal, not NER/DLP. The risky-text patterns are a backstop; the
  durable control is approval-gated replay of irreversible steps.

## 7. Cuts

Deliberate, with the seams left clean:

- **Operator console UI** — terminal + queue only; `IOperatorChannel` is the
  seam. Next: a web console consuming the queue, screen-sharing the session.
- **Desktop takeover recording** — the UIA3 adapter is proven end to end
  against a native app (§4), but it buffers no human actions during a
  takeover; the web surface does. Next: UIA event handlers for the
  focus/invoke/value-change patterns, written to the same evidence stream.
- **Tenant override resolver** — schema carries the block; the base+overlay
  merge and per-tenant catalog are not implemented.
- **LLM-assisted recovery on replay failure** — bounded, policy-checked
  single-step re-discovery when a chain exhausts; the hard-failure evidence
  dump is exactly its input.
- **Stability scoring** — replay N times, score flakiness, gate approval;
  fallback-rank logging is the hook.
- **Session recovery breadth** — only session-expiry re-auth is implemented;
  production needs a per-app taxonomy of recoverable session states.
