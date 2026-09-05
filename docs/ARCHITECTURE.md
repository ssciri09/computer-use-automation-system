# Architecture

Visual companion to [REPORT.md](../REPORT.md). Diagrams render inline on
GitHub. They describe the system as built and exercised — three surfaces, one
replay engine, five outcome classes proven live on each.

---

## 1. The through-line

Two passes, and everything follows from keeping them apart. Discovery is
expensive, non-deterministic and needs judgment. Replay is cheap, repeatable
and needs none. The artifact is the contract between them, and human approval
is the only door from one into the other.

```mermaid
flowchart LR
  subgraph D["DISCOVERY — once, with a model"]
    direction LR
    G["Goal<br/><i>natural language</i>"] --> L["Discovery loop<br/>observe → decide → act<br/><i>every action policy-gated</i>"]
    L --> T["Trace + declaration<br/><i>what executed (fact)</i><br/><i>+ what it means (judgment)</i>"]
    T --> C["Compiler"]
    C --> A1["Artifact<br/><b>draft</b>"]
  end

  A1 -->|"a human reviews and approves"| A2

  subgraph P["PRODUCTION — many times, no model"]
    direction LR
    A2["Artifact<br/><b>approved</b>"] -->|inputs| R["Replay engine<br/>resolve → act → wait → classify"]
    R --> RC["Result contract<br/><i>one of four classes</i>"]
    R -.->|"cannot continue safely"| H["Human takes<br/>the live session"]
    H -.->|"hands control back;<br/>checkpoint re-verifies"| R
  end

  RC -->|returns| AG["Calling agent<br/><i>invoked it by name over MCP</i>"]
```

**The artifact is the seam.** Discovery may be slow and non-deterministic
because it happens once. Everything downstream of the approval gate is
deterministic. Nothing crosses that gate without a person reading it.

| | |
|---|---|
| **trace** | Ground truth, mechanically recorded: which element actually resolved, its id, name, text and position, in which frame. Steps and locators are facts, not model prose. |
| **declaration** | Judgment, declared at record time: which conditions mean success, which are legitimate business answers, which are transient, which need a human. Never inferred during replay. |
| **transcript** | Kept as evidence only. Nothing in the raw model conversation reaches the artifact. |

---

## 2. Assemblies and the direction of dependency

Six assemblies, one-way references, enforced by the compiler rather than by
discipline.

```mermaid
flowchart TD
  CLI["Cua.Cli<br/><i>discover · replay · approve · list</i>"]
  MCP["Cua.Mcp<br/><i>catalog as agent-callable tools</i>"]
  ENG["Cua.Engine<br/><b>Discovery</b> — model in the loop<br/><b>Replay</b> — no model, ever"]
  HOST["Cua.Hosting<br/><i>composition root: picks the adapter by kind</i>"]
  WEB["Cua.Web<br/><i>Playwright</i>"]
  DESK["Cua.Desktop<br/><i>UIA3</i>"]
  CORE["Cua.Core<br/>artifact schema · result contract · policy gate<br/>redaction · evidence · HITL primitives<br/><b>ISurface — the perception / action seam</b>"]

  CLI --> ENG
  CLI --> HOST
  MCP --> ENG
  MCP --> HOST
  HOST --> WEB
  HOST --> DESK
  ENG --> CORE
  HOST --> CORE
  WEB --> CORE
  DESK --> CORE
```

**`Cua.Engine` holds both execution paths and shares nothing but Core with the
adapters.** It has no reference to Playwright or UIA — that was a stale project
reference until it was removed, so the claim is now checked at build time
rather than asserted in a document.

---

## 3. One vocabulary, three surfaces

Everything above `ISurface` speaks frame paths, ranked locator chains and
conditions. What those *mean* is the adapter's business.

```mermaid
flowchart TD
  V["<b>Artifact vocabulary</b><br/>frame path · ranked locator chain · condition · wait · risk<br/><i>surface-neutral by construction — no CSS, no window handles</i>"]
  V --> LW["Legacy web<br/><i>framesets · generated ids</i>"]
  V --> MW["Modern web<br/><i>semantic · published hooks</i>"]
  V --> DT["Desktop<br/><i>native window tree · UIA3</i>"]
```

| Artifact concept | Legacy web | Modern web | Desktop |
|---|---|---|---|
| frame path | iframe name path | single document | window / pane path |
| rank 1 | `#id` | **`[data-testid]`** | `AutomationId` |
| rank 2 | `[name=…]` | `[aria-label]` | `Name` |
| rank 3 | visible text | `#id` · role | — |
| last resort | coordinates | coordinates | coordinates |

**The ranking inverts between the two web kinds, and that is the whole point of
the distinction.** On a modern app a test id is a contract its author published
for automation, so it outranks the id. On a legacy app no such hook exists and
generated `ext-genNN` ids churn between vendor releases, so text and position
carry the fallback weight. Coordinates sit last everywhere — the one rank that
still resolves on a surface with no structure at all.

The same capability is recorded once per surface as sibling artifacts with
identical inputs and outputs; `cua list` shows one capability, three bindings.

---

## 4. What a replayed step decides

The interesting failures in a bank are not layout drift — these screens barely
change. They are the runtime conditions: a congested host link, a closed
account, a supervisory hold, a dead session.

```mermaid
flowchart LR
  ACT["Resolve + act<br/><i>locator chain</i>"] --> W["Wait<br/><i>busy signal clears</i>"]
  W --> E{"Evaluate declared<br/>conditions, in order"}

  E -->|matched| REC["<b>recoverable</b><br/>retry with declared<br/>backoff, bounded"]
  E -->|matched| BO["<b>business_outcome</b><br/>terminal, returns<br/>an outcome code"]
  E -->|matched| ESC["<b>escalate</b><br/>hand the live session<br/>to a human"]
  E -->|matched| OK["<b>success</b><br/>proceed to<br/>the next step"]
  E -->|"nothing matched<br/>before the timeout"| HF["<b>hard_failure</b><br/>step, expected vs observed,<br/>screenshot, state dump"]

  REC -.->|re-execute the step| ACT
```

**There is deliberately no `hard_failure` condition to declare.** A hard failure
is the *absence* of any recognised outcome when the step times out — never
something matched by a pattern. That makes the classic mistake, conflating "no
such account" with a crash, impossible to express in an artifact.

| Result class | Meaning |
|---|---|
| `success` | Checkpoint verified independently of the last click. Declared outputs returned. |
| `business_outcome` | **An answer, not an error.** "Account is closed" is what the caller asked to find out. |
| `escalation_pending` | Parked for an operator with full context. The run is not failed — it is waiting. |
| `hard_failure` | The state was unrecognised. Carries step, expected, observed and evidence to debug. |

> **One rule the engine enforces over the artifact.** A transient matched on an
> **irreversible** step never retries. The fee may already have posted, so
> re-clicking risks a double reversal. It escalates instead, and the checkpoint
> re-verifies state after handback. An artifact cannot opt out of this.

---

## 5. Handing over the live session

The hard part of human-in-the-loop is not detecting "stuck" — it is that the
human must land in *the same session*, with the same cookies and the same
half-finished transaction. A fresh window cannot clear a modal that only exists
in the one the automation was driving.

```mermaid
sequenceDiagram
    participant R as Replay engine
    participant S as SessionControl<br/>(holder token)
    participant O as Human operator
    participant A as The one live session

    R->>A: drives (holds the token)
    Note over R,A: assertion on every action:<br/>automation must hold control
    R->>S: escalate condition matched
    S->>O: intervention request<br/>goal · step · reason · screenshot · redacted digest
    S-->>R: token transferred — engine now refuses to act
    O->>A: operates the same window<br/>actions recorded, PINs masked
    O->>S: hands control back
    S-->>R: token returned
    R->>A: resume at declared step, re-verify state
    Note over R,A: the checkpoint proves the goal state,<br/>rather than trusting the human finished
```

**Control is a token, not a convention.** Attended mode gives the operator the
real window; unattended mode persists the intervention request and returns
`escalation_pending` with evidence preserved — the same seam, a different
operator surface.

---

## 6. Where a fee waiver meets the guardrails

One click through every layer. The model under-declared it as safe; it still
could not run unattended without a person having read the recording first.

```mermaid
flowchart LR
  CLICK["click<br/><i>Waive Fee</i>"] --> G1
  G1["<b>1 · Allowlist</b><br/>host, port and route,<br/>or app identity on desktop<br/><i>checked before every action</i>"] --> G2
  G2["<b>2 · Risk upgrade</b><br/>declared: safe<br/>text matched 'waive'<br/>→ irreversible<br/><i>never downgrades</i>"] --> G3
  G3["<b>3 · Double key</b><br/>artifact approved by a human<br/>AND caller passed --ack-risk<br/><i>else: ask a human</i>"] --> G4
  G4["<b>4 · Ambiguity</b><br/>host congested after the click:<br/>did it post?<br/><i>never retried — escalates</i>"] --> G5
  G5["<b>5 · Failover</b><br/>reads may re-route<br/>to another surface;<br/>writes never cross this line"]
```

**The durable control is gate 3, not the keyword list.** Text patterns are an
English-language heuristic and a determined counter-example gets past them; "a
human read this exact recording and approved it" does not depend on guessing
what a button is called.

Secrets never enter the artifact: it stores a credentials *reference*, and
resolved values are registered with the redactor before the first log line is
written. Persisted screen captures get a stricter tier that also masks
account-shaped digit runs, protecting bystanders visible on a shared screen.

---

## 7. What the calling agent sees

The catalog is served over MCP. Approved artifacts become callable tools whose
schemas are derived from their own typed inputs; every artifact, drafts
included, stays readable as a resource.

```mermaid
sequenceDiagram
    participant AG as AI agent
    participant M as cua-mcp<br/>(stdio, local)
    participant E as Replay engine
    participant APP as Live application

    AG->>M: tools/list
    M-->>AG: one tool per approved binding,<br/>JSON Schema from the artifact's typed inputs
    AG->>M: tools/call firstcore_fee_waiver__firstcore_modern<br/>{"account_id": "99999"}
    M->>E: replay the approved artifact
    E->>APP: deterministic steps, no model
    APP-->>E: "Account is closed."
    E-->>M: business_outcome / not_permitted
    M-->>AG: isError: false + structured result
    Note over AG,M: a closed account is the answer<br/>the agent asked for, not a failure to retry
```

**Callable and reviewable are deliberately different sets** — the approval gate
expressed in the protocol. A capability that exists only as a draft is refused
by name, stating its approval state.

The transport is stdio: the client spawns the server as a child process, so
there is no listening socket. That is forced by the work — the server drives a
real browser or a real window and must be on the machine that owns that session.
`CapabilityServer` takes a `TextReader`/`TextWriter`, so a remote HTTP
transport swaps in without touching catalog, tool generation or invocation.

---

## 8. Scaling the idea, not the infrastructure

Hundreds of institutions run the same vendor products, configured and branded
differently. The unit of reuse is the vendor-product artifact, never the tenant
recording.

| | |
|---|---|
| **`app_binding`** | One capability can exist on the web portal, a legacy console *and* the thick client. Sibling recordings version independently, so a desktop recording is never mistaken for the web artifact's next version. |
| **tenant overrides** | A new tenant replays the base artifact supervised. Locator fallback hits and hard failures identify *which steps* need specialising — recorded as sparse overrides, never a fork. |
| **drift signal** | Every replay logs which locator rank actually resolved. A tenant drifting onto fallback ranks is flagged for re-validation *before* it breaks. |
| **routing** | Callers ask for a capability; a per-tenant policy picks the binding deterministically before the run starts — pin, then approval state, then replay-stability history. |

**Deliberately not built:** the tenant override resolver, the routing engine,
stability scoring and a real operator console. Each has a clean seam and none is
load-bearing for the thesis; building that infrastructure before the
abstractions are proven would be the more expensive mistake. See REPORT §7.
