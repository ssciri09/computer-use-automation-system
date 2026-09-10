# Architecture

This document explains the system visually. The short assignment write-up is
in [REPORT.md](../REPORT.md).

## 1. System at a glance

The central design decision is to separate **discovery** from **replay**.

```mermaid
flowchart LR
    U["Natural-language goal<br/>+ target + example inputs"]

    subgraph DISCOVERY["1. DISCOVERY — model used once"]
        direction LR
        LOOP["Observe → decide → act"]
        TRACE["Mechanical trace<br/>what actually happened"]
        DECL["Semantic declaration<br/>what outcomes mean"]
        COMP["Artifact compiler<br/>+ validator"]
        DRAFT["Draft capability artifact"]

        LOOP --> TRACE
        LOOP --> DECL
        TRACE --> COMP
        DECL --> COMP
        COMP --> DRAFT
    end

    REVIEW["Human review<br/>and approval"]

    subgraph REPLAY["2. REPLAY — no model"]
        direction LR
        INPUT["Invocation inputs"]
        ENGINE["Policy → resolve → act<br/>→ wait → classify"]
        RESULT["Structured result"]

        INPUT --> ENGINE --> RESULT
    end

    CALLER["Calling AI agent<br/>CLI or MCP"]

    U --> LOOP
    DRAFT --> REVIEW
    REVIEW -->|"approved artifact"| ENGINE
    CALLER --> INPUT
    RESULT --> CALLER
```

Discovery may use judgment and exploration. Replay cannot: it executes only
the reviewed artifact and returns a declared result.

### The artifact is the boundary

```mermaid
flowchart TB
    MODEL["Model conversation"]
    EXEC["Actions that really executed"]
    MEANING["Declared outcome meaning"]

    MODEL -.->|"evidence only"| TRANSCRIPT["Transcript"]
    EXEC --> TRACE["Trace"]
    MEANING --> DECL["Declaration"]
    TRACE --> COMPILER["Compiler"]
    DECL --> COMPILER
    COMPILER --> ARTIFACT["Executable artifact"]

    TRANSCRIPT -.->|"never executed"| ARTIFACT
```

- **Trace:** mechanically captured element metadata, action, frame, value
  reference, wait, and risk.
- **Declaration:** reviewer-visible meaning such as success, not found,
  transient, or escalation.
- **Transcript:** retained as evidence only; replay never interprets it.

## 2. Components and dependency direction

Concrete UI technology stays below `ISurface`. The engine does not reference
Playwright or UIA directly.

```mermaid
flowchart TB
    CLI["Cua.Cli<br/>discover · approve · replay · list"]
    MCP["Cua.Mcp<br/>agent-facing capability catalog"]
    HOST["Cua.Hosting<br/>composition root"]
    ENGINE["Cua.Engine<br/>discovery · compiler · replay"]
    WEB["Cua.Web<br/>Playwright adapter"]
    DESKTOP["Cua.Desktop<br/>UIA3 adapter"]
    CORE["Cua.Core<br/>artifact schema · result contract<br/>policy · redaction · evidence · HITL<br/>ISurface"]

    CLI --> ENGINE
    CLI --> HOST
    MCP --> ENGINE
    MCP --> HOST
    HOST --> WEB
    HOST --> DESKTOP
    HOST --> CORE
    ENGINE --> CORE
    WEB --> CORE
    DESKTOP --> CORE
```

### Responsibilities

- `Cua.Core` defines contracts and has no UI-framework dependency.
- `Cua.Engine` contains the two execution paths.
- `Cua.Web` translates surface-neutral operations into Playwright.
- `Cua.Desktop` translates them into Windows UI Automation.
- `Cua.Hosting` chooses the adapter stamped on the artifact.
- `Cua.Cli` provides the evaluator-facing workflow.
- `Cua.Mcp` exposes approved capabilities as typed agent tools.

## 3. What a capability artifact contains

The artifact is a capability contract, not a raw macro.

```mermaid
flowchart TB
    ART["CapabilityArtifact"]

    ART --> ID["Identity<br/>capability id · version<br/>vendor · app binding"]
    ART --> POLICY["Execution boundary<br/>surface · entry point<br/>allowlist · allowed actions"]
    ART --> CONTRACT["Agent contract<br/>typed inputs · typed outputs<br/>credential reference"]
    ART --> FLOW["Replay flow<br/>ordered steps · frames/panes<br/>locator chains · waits · risk"]
    ART --> OUTCOMES["Outcome semantics<br/>success · business outcome<br/>recoverable · escalation"]
    ART --> PROOF["Verification and governance<br/>checkpoint · extracts<br/>redaction · provenance · approval"]
```

Before compilation, approval, or replay, `ArtifactValidator` checks structural
and semantic invariants:

```mermaid
flowchart LR
    JSON["Artifact JSON"] --> VALIDATE{"Valid?"}
    VALIDATE -->|"yes"| EXECUTE["Approve or replay"]
    VALIDATE -->|"no"| REJECT["Reject before UI actions"]

    NOTE["Examples:<br/>duplicate step IDs<br/>empty locator chain<br/>invalid regex<br/>unknown resume target<br/>disallowed action<br/>invalid output enum"]
    NOTE -.-> VALIDATE
```

## 4. One replayed step

Every interactive step follows the same bounded sequence.

```mermaid
flowchart TD
    START["Start step"]
    GUARD{"Global guard matched?"}
    POLICY{"Policy permits action<br/>in current app/route?"}
    RESOLVE{"Resolve ranked<br/>locator chain"}
    RISK{"Effective risk?"}
    CONFIRM{"Approved artifact<br/>+ risk acknowledgement?"}
    ACT["Execute action"]
    WAIT["Apply declared wait"]
    ASSERT{"Evaluate declared<br/>conditions in order"}

    START --> GUARD
    GUARD -->|"session expired"| AUTH["Run auth phase once"] --> START
    GUARD -->|"escalate"| HUMAN["Human intervention"]
    GUARD -->|"no match"| POLICY

    POLICY -->|"blocked"| FAIL["Hard failure<br/>with evidence"]
    POLICY -->|"allowed"| RESOLVE

    RESOLVE -->|"none matched"| FAIL
    RESOLVE -->|"target found"| RISK

    RISK -->|"safe / write"| ACT
    RISK -->|"irreversible"| CONFIRM
    CONFIRM -->|"authorized"| ACT
    CONFIRM -->|"not authorized"| HUMAN

    ACT --> WAIT --> ASSERT
    ASSERT -->|"success"| NEXT["Next step"]
    ASSERT -->|"business outcome"| BUSINESS["Return legitimate outcome"]
    ASSERT -->|"recoverable"| RETRY{"Safe to retry?"}
    ASSERT -->|"escalate"| HUMAN
    ASSERT -->|"nothing matched by timeout"| FAIL

    RETRY -->|"yes, bounded backoff"| START
    RETRY -->|"irreversible / ambiguous"| HUMAN
```

### Result meanings

```mermaid
flowchart LR
    OUTCOME{"Replay result"}
    OUTCOME --> SUCCESS["success<br/>checkpoint verified<br/>outputs returned"]
    OUTCOME --> BUSINESS["business_outcome<br/>valid answer such as<br/>account not found"]
    OUTCOME --> PENDING["escalation_pending<br/>operator context queued"]
    OUTCOME --> FAILURE["hard_failure<br/>unknown or unsafe state<br/>debug evidence attached"]
```

A hard failure is intentionally not declarable as a matching condition. It is
the absence of any recognized outcome before timeout.

## 5. Locator chains and surface abstraction

The artifact uses one vocabulary; each adapter interprets it for its surface.

```mermaid
flowchart TB
    STEP["Artifact step<br/>frame path + locator chain<br/>condition + wait + risk"]
    SURFACE["ISurface"]

    STEP --> SURFACE
    SURFACE --> LEGACY["Legacy web<br/>named nested frames<br/>generated IDs<br/>non-semantic controls"]
    SURFACE --> MODERN["Modern web<br/>semantic DOM<br/>test IDs · ARIA"]
    SURFACE --> NATIVE["Windows desktop<br/>UIA tree<br/>windows · panes"]
```

### Resolution order

```mermaid
flowchart LR
    subgraph LEGACY["Legacy web"]
        L1["id"] --> L2["name"] --> L3["scoped text"] --> L4["coordinates"]
    end

    subgraph MODERN["Modern web"]
        M1["data-testid"] --> M2["aria-label"] --> M3["id / name"] --> M4["role + text"] --> M5["coordinates"]
    end

    subgraph DESKTOP["Desktop"]
        D1["AutomationId"] --> D2["Name"] --> D3["coordinates"]
    end
```

The first candidate that resolves wins. Using a non-primary candidate emits a
`locator_fallback` event. That event is both graceful degradation and a future
drift signal.

Coordinates are deliberately last. They tolerate surfaces with little
structure but are sensitive to window position, scaling, and layout.

## 6. Human escalation and control transfer

### Attended mode: implemented same-session handoff

```mermaid
sequenceDiagram
    participant E as Replay engine
    participant C as SessionControl
    participant O as Operator
    participant S as Live browser session

    E->>S: Drive application
    E->>C: Escalation condition matched
    C->>C: Holder = Human
    C-->>E: Automation actions now rejected
    E->>O: Goal, step, reason, redacted state, resume plan
    O->>S: Operate the same window and cookies
    S-->>E: Trusted human events buffered
    O->>C: Signal handback
    C->>C: Holder = Automation
    E->>S: Resume at declared step
    E->>S: Verify checkpoint independently
```

The control token is restored in `finally`, so an operator error cannot leave
automation permanently locked out.

The console channel accepts handback through stdin. The signal-file channel
supports an external/mock operator coordinator: it persists the intervention
request and keeps the process and human control token live until a matching
`.resume` signal arrives. Both channels use the same session and action
recorder; only the handback transport differs.

### Unattended queue: intentionally limited

```mermaid
flowchart LR
    ENGINE["Replay engine"] --> REQUEST["Persist redacted<br/>intervention request"]
    REQUEST --> RESULT["Return<br/>escalation_pending"]
    RESULT --> END["Surface disposed"]

    FUTURE["Future operator service<br/>would own surface lifetime<br/>and provide resume/cancel"]
    REQUEST -.-> FUTURE
```

The built-in queue is notification-only. It preserves context and evidence,
not the live browser process. The real handoff required by the assignment is
the attended path above.

## 7. Safety and evidence boundaries

```mermaid
flowchart TD
    INTENT["Proposed or replayed action"]
    ALLOW["1. Target allowlist<br/>host · route · desktop identity"]
    ACTIONS["2. Allowed action types"]
    UPGRADE["3. Risk upgrade<br/>resolved control text"]
    APPROVAL["4. Artifact approval<br/>+ caller acknowledgement"]
    EXEC["Execute"]

    INTENT --> ALLOW --> ACTIONS --> UPGRADE --> APPROVAL --> EXEC
    ALLOW -->|"outside boundary"| BLOCK["Block"]
    ACTIONS -->|"not permitted"| BLOCK
    APPROVAL -->|"irreversible and unauthorized"| HUMAN["Human confirmation"]
```

### Data handling

```mermaid
flowchart LR
    CREDS["Credential reference<br/>env://CUA"]
    RUNTIME["Resolve only at runtime"]
    REDACT["Register secret/input literals<br/>with redactor"]
    LOGS["Redacted JSONL logs<br/>and observation dumps"]

    CREDS --> RUNTIME --> REDACT --> LOGS

    PIXELS["Raw screenshots"]
    SYNTH{"Explicit<br/>--synthetic-evidence?"}
    PIXELS --> SYNTH
    SYNTH -->|"no"| SKIP["Do not persist"]
    SYNTH -->|"yes, fabricated demo only"| SAVE["Persist PNG evidence"]
```

Redaction fails closed if a regex exceeds its timeout. Caller outputs remain
available to the authorized caller, while persisted evidence is redacted.

## 8. Multi-tenant reuse at scale

The design reuses artifacts by vendor product and compatible application
binding rather than recording every capability separately for every tenant.

```mermaid
flowchart TB
    BASE["Vendor capability base<br/>FirstCore fee waiver"]
    VERSION["Vendor-version overlay<br/>shared workflow difference"]
    SURFACE["Surface/config overlay<br/>legacy · modern · desktop"]
    TENANT["Sparse tenant override<br/>only exceptional steps"]
    FINAL["Resolved tenant binding"]

    BASE --> VERSION --> SURFACE --> TENANT --> FINAL
```

### Tenant onboarding

```mermaid
flowchart LR
    NEW["New tenant/app instance"]
    SELECT["Select vendor, version,<br/>surface base"]
    PREFLIGHT["Supervised preflight<br/>use test data<br/>stop before unsafe writes"]
    MATCH{"Compatible?"}
    USE["Use shared artifact"]
    FALLBACK["Fallback works<br/>log drift signal"]
    OVERRIDE["Record sparse<br/>approved override"]
    PROMOTE{"Same difference<br/>across many tenants?"}
    SHARED["Promote to shared<br/>version/config overlay"]

    NEW --> SELECT --> PREFLIGHT --> MATCH
    MATCH -->|"primary locators work"| USE
    MATCH -->|"fallback locators work"| FALLBACK --> USE
    MATCH -->|"specific steps differ"| OVERRIDE --> USE
    OVERRIDE --> PROMOTE
    PROMOTE -->|"yes"| SHARED
```

Example for five tenants using one product across three surfaces:

```mermaid
flowchart TB
    L["Legacy-web base"]
    M["Modern-web base"]
    D["Desktop base"]

    A["Tenant A"] --> L
    B["Tenant B<br/>fallback only"] --> L
    C["Tenant C<br/>one label override"] --> M
    E1["Tenant E"] --> M
    E2["Tenant E"] --> D
    F["Tenant D<br/>older version overlay"] --> D
```

Implemented today:

- vendor metadata and application bindings;
- independent versions per capability/binding;
- ranked locator fallback;
- fallback logging and structured failures;
- a tenant metadata/override seam.

Design-only, as allowed by the assignment:

- tenant-aware routing;
- typed overlay merge and approval;
- tenant-scoped credential service;
- centralized drift aggregation;
- fleet scheduling and storage.

## 9. End-to-end sequence

```mermaid
sequenceDiagram
    participant Dev as Capability author
    participant LLM as Discovery model
    participant UI as Target application
    participant Store as Artifact store
    participant Agent as Calling agent
    participant Replay as Replay engine

    Dev->>LLM: Goal + target + synthetic parameters
    loop Observe, decide, act
        LLM->>UI: Policy-approved action
        UI-->>LLM: Observation
    end
    LLM-->>Store: Trace + declaration
    Store-->>Dev: Draft artifact for review
    Dev->>Store: Approve

    Agent->>Replay: Capability name + typed inputs
    Replay->>Store: Load approved artifact
    loop Deterministic steps
        Replay->>UI: Policy-approved recorded action
        UI-->>Replay: Declared state or error
    end
    Replay-->>Agent: Structured result + outputs
```

The model discovers. The artifact is the reusable contract. Replay is the
production execution path.
