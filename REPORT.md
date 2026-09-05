# Design write-up

## 1. Architecture

Four assemblies with one-way dependencies, arranged around two seams:

```
Cua.Cli ──► Cua.Engine ──► Cua.Web ──► Playwright
                │              │
                └──────► Cua.Core ◄────┘   (schema, contracts, policy, redaction, HITL, ISurface)
```

- **`Cua.Core`** is dependency-free and owns everything both sides of the
  system share: the artifact schema, the result contract, the policy gate, the
  redactor, the evidence logger, the HITL primitives, and `ISurface` — the
  perception/action seam. Discovery and replay speak only these types.
- **`Cua.Web`** is the only assembly that knows a browser exists.
- **`Cua.Engine`** contains the two execution paths: the LLM discovery loop
  and the deterministic replay engine. They share nothing but Core — replay
  cannot accidentally grow a model dependency.

Key decisions and trade-offs:

- **Manual model loop, not an agent framework.** Every model-proposed action
  passes the `PolicyGate` *between decision and execution*. The model never
  holds a handle to the surface; it emits intents, the engine validates and
  executes. That gate placement is the safety architecture, and it is why I
  didn't use an SDK-managed tool runner.
- **Mechanical trace + declared semantics.** During discovery the engine
  records ground truth for every executed action (which element resolved, its
  id/name/text/position, which frame). The model separately *declares* the
  semantic layer at the end (`declare_capability`): outcome classification,
  checkpoint, outputs, auth steps. The compiler merges the two. Steps and
  locators are therefore facts, not model prose; judgment is captured where
  judgment is needed — and it is reviewable before approval.
- **Single process, no queues.** The realistic scale unit is one run; the
  interesting complexity is in the artifact and replay semantics, not in
  infrastructure. The operator channel and artifact store are interfaces a
  production deployment would re-implement (queue-backed console, database
  catalog) without touching the engine.
- **Observation is a compact digest, not screenshots.** Each frame is reduced
  to fields/controls/messages/tables with ids and text (~2–4 KB). On a legacy
  app with a hostile DOM this is more token-frugal and more *actionable* than
  screenshots, and the same digest shape is producible from an accessibility
  tree (see §4). Screenshots are still captured as evidence and escalation
  context.

## 2. Artifact schema

An artifact is a **capability contract**, not a macro. Top level:
identity/versioning, vendor + tenant blocks, surface (kind, entry URL,
allowlist), typed `inputs` (with sensitivity + redaction flags), typed
`outputs` (enums for outcome codes), a credentials *reference* (never values),
`guards`, ordered `steps`, redaction spec, and provenance
(model, discovery run id, `draft`/`approved` state).

Shaping decisions:

- **Frame paths are part of every locator** (`"frame": ["wrkFrm","modFrm"]`).
  On frameset-era apps the frame is half the address; making it ambient state
  is how replays end up asserting against the wrong document. Conditions can
  override the frame per assertion — the security modal is asserted in the
  *top* document precisely because the app injects it there.
- **Locators are ranked chains with recorded reasoning:** id → name attribute
  → scoped visible text → frame-relative coordinates. `ext-genNN` ids churn
  across vendor releases, so every rank below id is a genuine fallback, built
  from resolution-time metadata (not guessed). Replay logs when a fallback
  rank was needed — that signal is the drift detector (§4).
- **Assertions are declared per step, in evaluation order**, each classified
  `recoverable` (with retry policy), `business_outcome` (with emitted
  outputs, terminal), `escalate` (with reason + resume-at), or `success`.
  There is deliberately **no `hard_failure` class**: a hard failure is the
  *absence* of any recognized condition at timeout, never a pattern match.
- **Waits are attached to the step that causes them** (`wait_after`, e.g.
  "busy spinner must disappear"), because sequencing on slow host calls is a
  property of the action, not a free-standing step.
- **Parameterization is mechanical.** Values the model typed that match a
  declared run parameter are compiled to `value_ref: inputs.account_id`;
  credential placeholders compile to `credentials.username` against the
  artifact's `env://`/`vault://` ref. The artifact never contains a concrete
  member id or secret.

## 3. Determinism & error handling

Replay executes steps in order with zero model involvement. Per step: resolve
the locator chain (bounded polling until the step timeout — this absorbs
late-injected iframes and slow renders), act, run the attached wait, then poll
the declared assertions in order until one matches or the timeout expires.

The result contract is a hard four-way split, because conflating these is the
classic failure:

| Status | Meaning | Carries |
|---|---|---|
| `success` | checkpoint verified | declared outputs (+ extraction regexes run over frame text) |
| `business_outcome` | the app legitimately said no ("no records found", "account is closed") | outcome code + outputs |
| `escalation_pending` | intervention raised, unattended | preserved context for the operator |
| `hard_failure` | no declared condition matched | step id, expected vs observed, screenshot + observation dump |

Runtime conditions, which are the interesting failures here (the UI is
stable):

- **Transients** (host-link congestion banner): matched as `recoverable`,
  re-execute the step with declared backoff, bounded attempts; exhaustion is a
  hard failure ("transient condition persisted"), never a silent loop.
- **Expected rejections**: matched as `business_outcome`, terminal, and
  reported as a *result* the calling agent can act on.
- **Unexpected dialogs / session expiry**: artifact-level `guards` checked
  before every step. The compiler mechanically emits a session-expiry guard
  (the recorded sign-on field reappearing ⇒ rerun the `auth`-phase steps once,
  then resume at the interrupted step); escalation guards hand off to a human.
- **Unknown states**: hard failure with expected-vs-observed, the on-screen
  messages, a screenshot, and a full observation dump — enough to debug
  without rerunning.

**Grounding declared conditions — a failure we found and fixed.** The first
live discovery run produced a structurally perfect artifact with a subtle
poison: the model had only *seen* the happy path, so it **invented** the text
for outcome conditions it never observed (`"HOST BUSY"` where the app actually
says `HOST-0521: Legacy host link congested`; `"ACCOUNT NOT FOUND"` where it
says `No records found…`). Those replays would hard-fail instead of
classifying — a hallucinated condition is worse than an undeclared one,
because it looks reviewable. The fix is in the system, not the prompt alone:
discovery actions accept a `probe` flag (exploratory, excluded from the
compiled flow), the agent is required to ground every declared condition in
observed text — probing variant inputs after the main flow to *trigger* each
error state and read its exact wording — and to leave unobservable states
undeclared. Run 1 is kept in `/evidence/` as the documented failure mode; the
shipped artifact comes from the probe-grounded run 2. The `draft → approved`
human review exists for exactly this class of defect.

UI drift (secondary here): the locator chain absorbs id churn; every fallback
hit is logged as a drift signal; `approval` + versioning make re-recording a
controlled event rather than a silent behavior change.

## 4. Heterogeneity & multi-tenant

**Surface abstraction.** Everything above `ISurface` speaks frame paths,
locator chains, conditions, and observations. The seam is: *how a target is
addressed and perceived* is the adapter's job; *what the flow is* belongs to
the artifact. A legacy web app is the implemented case (the mock is
deliberately hostile: framesets, generated ids, `<span onclick>` controls). A
desktop adapter maps the same vocabulary onto an accessibility tree — frame
path → window/pane path, css-id → AutomationId, text locator → name property,
coordinates stay coordinates — and produces the same observation digest from
UIA elements. The artifact's `surface.kind` selects the adapter; steps,
assertions, guards, and the replay engine are unchanged. Coordinates-as-
last-resort exists in the chain specifically because it is the one rank that
survives on surfaces with no structure at all (bitmap-only desktop screens).

**Multi-tenant reuse.** Hundreds of institutions run the same vendor product
configured differently, so the unit of reuse is the *vendor-product artifact*,
not the tenant recording. The artifact carries a `vendor` block (product +
version range) and a `tenant` block: recorded-for plus a sparse per-tenant
override map (entry URL, locator substitutions per step id, cosmetic drift).
Rollout: replay the base artifact against a new tenant in a read-only or
supervised mode; locator-fallback hits and hard failures at specific steps
tell you *which steps* need a tenant override — recorded as overrides, never
as a fork. Drift management is the same signal over time: a tenant whose
replays increasingly resolve on fallback ranks is flagged for re-validation
before it breaks. Version-range bumps from the vendor trigger re-recording
once, centrally, not per tenant.

## 5. Escalation & handoff

**Detecting "stuck".** Three routes: (1) declared `escalate` assertions —
conditions the discovery run identified as human-needed (the security
interception modal, asserted in the top frame); (2) guards for unexpected
states; (3) the discovery agent itself has an `escalate_to_human` tool for
when it is stuck live. Irreversible steps without standing authorization also
route through the same mechanism as confirmation requests (§6).

**Routing with context.** An `InterventionRequest` carries capability + goal,
current step, reason, a screenshot, a redacted observation digest, and the
resume plan. Attended mode presents it at the operator terminal; unattended
mode persists it to an operator queue and the run ends `escalation_pending`
with the session evidence preserved.

**Control transfer is explicit and enforced.** `SessionControl` is a
holder-token state machine (`Automation` ⇄ `Human`). While a human holds
control the engine *cannot* drive the surface (asserted on every action), and
the surface's recorder — an init script in every frame reporting only trusted
(`isTrusted`) events — buffers what the human does: clicks and field changes,
with password/PIN values masked. It is the **same live browser session**: same
cookies, same in-page state, which is the point (the override modal exists
only there). On handback, automation re-takes the token, the human's actions
are written to evidence, and the run resumes at the declared `resume_at` step
(typically the checkpoint) which re-verifies state rather than trusting that
the human finished the job.

**What is mocked:** the operator *presentation* (terminal + queue directory
instead of a web console). The seam (`IOperatorChannel`), the control-transfer
model, the context payload, action recording, and resume semantics are real
and unit-tested.

## 6. Safety

- **Allowlist enforcement.** Host allowlist checked on every navigation *and*
  on the current page before every action, discovery and replay alike. The
  model can request anything; the gate refuses and tells it why
  (`POLICY_BLOCKED` results are visible in the transcript as evidence).
- **Risky actions.** Risk is declared per step, and independently *upgraded*
  by target-text patterns (Waive/Commit/Place/Reverse/…) so a model that
  under-declares still gets caught; risk can never be downgraded. Discovery
  default is `flag` (execute + record prominently — the goal itself implies
  the write); `confirm`/`block` are one flag away. On replay, irreversible
  steps execute unattended only when the artifact is **approved** (a human
  reviewed the draft) *and* the caller passed `--ack-risk`; otherwise the step
  becomes a human confirmation via the escalation channel. Draft artifacts
  refuse to replay at all without `--allow-draft`.
- **Secrets and PII.** Artifacts store credential *references*; values resolve
  from the environment (vault in production), are registered with the redactor
  as literals, and the model only ever sees `{{credential:*}}` placeholders.
  Redaction is applied at the persistence boundary (every log line, transcript
  entry, saved text), so nothing downstream has to remember to be careful:
  secret literals, key/token/SSN patterns, replay input values marked
  `redact_in_logs`, and confirmation-id patterns (outputs go to the caller,
  not into logs). Password fields are masked at observation time.
- **Limits.** Discovery necessarily exposes on-screen data and parameter
  values to the model — which is why discovery runs against test/sandbox data,
  while replay (the production path) sends nothing anywhere. Redaction is
  pattern+literal based, not NER; a production system would put a DLP pass at
  the same boundary. The risky-text patterns are a heuristic backstop, not a
  guarantee — the durable control is approval-gated replay of irreversible
  steps.

## 7. Cuts

Deliberate, with the seam left clean:

- **Operator console UI** — cut; `IOperatorChannel` (terminal + queue) is the
  seam. Next: a small web console consuming the queue, screen-sharing the
  headed session (CDP), approve/deny for risk confirmations.
- **Desktop surface** — designed (§4), not built. Next: a UIA-based `ISurface`
  and one artifact replayed across web + desktop builds of the same flow.
- **Tenant overrides** — schema carries the block; the resolver
  (base + overlay merge, per-tenant catalog) is not implemented. Next: record
  on a second app variant and demonstrate one artifact + sparse overrides.
- **LLM-assisted recovery on replay failure** — a bounded, policy-checked
  single-step re-discovery when a locator chain exhausts, recorded as a
  proposed artifact patch. High value, cut for scope; the hard-failure
  evidence dump is exactly its input.
- **Stability scoring** — replay N times, score flakiness, gate approval on
  it. The `approval` state and fallback-rank logging are the hooks.
- **Session recovery breadth** — only session-expiry re-auth is implemented
  (mechanically compiled guard); a production system needs a taxonomy of
  recoverable session states per app.
