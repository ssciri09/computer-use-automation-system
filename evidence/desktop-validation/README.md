# Desktop surface validation

The same replay engine and the same artifact vocabulary, driven against the
native WinForms build (`mock-legacy-desktop/`) through the UIA3 adapter — no
browser involved. Artifact:
`capabilities/firstcore.fee_waiver.firstcore-desktop.v1.json`.

`sweep.log` is the raw output of all five runs. Per-run JSONL logs and
`result.json` are in the `replay-20260905-2147*` directories.

| Scenario | Input | Result |
|---|---|---|
| Happy path | `12345` | `success`, `outcome=waived`, `confirmation_number=RVSL-4D283E90` |
| Closed account | `99999` | `business_outcome`, `outcome=not_permitted` (a result, not a crash) |
| Unknown account | `77777` | `business_outcome`, `outcome=not_found` |
| Host congestion | `55555` | `recoverable` matched twice → backoff 2s, 4s → `success`, `RVSL-F01F7645` |
| Security interception | `00000` | `escalation_pending` — the assertion names the **root** frame, because the overlay is painted outside the module pane |

What this demonstrates that the web runs cannot:

- artifact locators are surface-neutral: `css #id` resolves to an
  `AutomationId`, text locators to the UIA `Name` property
- frame paths are surface-neutral: `["WorkspacePane","FeeModulePane"]`
  addresses a nested pane path exactly as `["wrkFrm","modFrm"]` addresses
  nested iframes
- the outcome taxonomy, retry policy, checkpoint verification, and escalation
  routing are properties of the engine, not of the browser
