# Scenario reference

Six modules, all live. Behaviour is keyed off the account identifier (and in one
case the check number or business date), so a small fixed input set drives every
outcome class across every workflow.

## The account keys

| Account | Meaning |
|---|---|
| `12345` | Healthy active account. The happy path everywhere. |
| `55555` | Host link congests on the first two calls, succeeds on the third. |
| `99999` | Closed account. Reads work; writes are rejected. |
| `00000` | Restricted party. Writes trigger a security interception. |
| anything else | No records found. |

The `55555` counter is **per session and per module**, so each workflow exercises
the retry path independently. Restarting the server resets it.

## Modules

### Balance Inquiry (`Inquiry > Balance Inquiry`)
Read-only. Enter an account, click **Retrieve Balances**.

- `12345` — ledger, available, and hold amounts render in `#ext-gen59`
- `55555` — congests twice
- `99999` — renders, plus a notice that balances are as of closure
- `00000` — balances replaced by `*** RESTRICTED ***` with an information-barrier
  warning. **A read that succeeds but returns no usable value** — worth handling
  distinctly from a failure.
- unknown — no records found

### Transaction History (`Inquiry > Transaction History`)
Read-only with a `<select>` period filter and pagination.

- `12345` — 8 rows across 2 pages. Exercises `select_option` and a **Next Page**
  control that only exists when there is a next page.
- `99999` — "No activity in the selected period."
- `55555` — congests twice

### Stop Payments (`Account Servicing > Stop Payments`)
Two-step write: **Validate Item**, then **Place Stop Payment**.

- `12345` + any check — validates, then commits with an `SP-` confirmation
- check number `000000` — "Check has already cleared." Rejected at *validate*,
  before the irreversible step.
- `99999` — "Account is closed. Stop payment cannot be placed."
- `00000` — security interception on commit; PIN `7391` clears it and the flow
  resumes
- `55555` — congests twice on validate

### Fee Management (`Account Servicing > Fee Management`)
The original workflow. Inquiry, then **Waive Fee**.

- `12345` — success, `RVSL-` confirmation
- `55555` — congests twice on inquiry
- `99999` — inquiry succeeds; waive returns "Account is closed. Fee waiver not
  permitted."
- `00000` — inquiry succeeds; waive throws the security modal

### Address Maintenance (`Account Servicing > Address Maintenance`)
Three-step write: **Load Address**, edit fields, **Review Changes**, **Commit
Changes**. The commit button does not exist until review is clicked.

- `12345` — full round trip, `ADR-` confirmation
- `99999` — rejected at commit
- `00000` — security interception at commit
- blank State or a ZIP under 5 characters — validation rejection

### Daily Fee Register (`Reports > Daily Fee Register`)
Date-driven report. Slowest call in the app (~5s).

- `08/26/2026` — 3 items with a total line
- `08/25/2026` — 1 item
- any other date — "No fees assessed for the selected business date."
- blank — "Business date is required."

## Why the decoys are real

An agent that misroutes now lands on a working screen with a different input
contract, which is what happens in a real bank app. It has to notice it is in the
wrong module and back out. A dead link that silently does nothing is an easier
and less realistic failure.

## Deliberate obstacles

- **Two levels of iframe.** `top → wrkFrm → modFrm`. The module frame is injected
  by JavaScript ~1.2s after the menu click, so it does not exist at page load.
- **Sign-on and a disclaimer modal** gate the shell.
- **The nav tree expands only via the `+` glyph.** Clicking a folder label selects
  it and nothing else.
- **Controls are `<span>` and `<div>` with `onclick`** — no `<button>`, no `role`,
  no `aria-label`.
- **IDs are auto-generated legacy junk** (`ext-gen42_input`, `ext-comp-2030`).
- **Layout is nested tables** with `x-grid3` / `x-grid-cell-inner` classes.
- **Result grids and action buttons do not exist** until a query resolves.
- **Every host call injects `#loading-spinner`** for 3–5 seconds. Wait for the
  spinner to *disappear*, not for an element to appear.
- **The security modal is written into `window.top.document`**, not the module
  frame, so an agent scoped to `modFrm` sees nothing change.
- **Multi-step writes hide their commit button** behind a validate or review step.

## Latency

```bash
LATENCY_SCALE=0.2 python3 server.py    # 5x faster while iterating
```

Leave it at `1.0` for anything going into `/evidence/`.
