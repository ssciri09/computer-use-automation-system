# Modern-web surface validation

Third binding of the same capability, against `mock-modern-web/` — one
document, no frames, semantic elements, `data-testid` hooks, `aria-live`
status region, native `<dialog>` modal. Artifact:
`capabilities/firstcore.fee_waiver.firstcore-modern.v1.json`.

| Scenario | Input | Result |
|---|---|---|
| Happy path | `12345` | `success`, `waived`, `RVSL-9D462A34`, `account_name=M R HOLLOWAY` |
| Closed account | `99999` | `business_outcome` → `not_permitted` |
| Unknown account | `77777` | `business_outcome` → `not_found` |
| Host congestion | `55555` | recoverable ×2 → backoff 2s, 4s → `success`, `RVSL-CC0FCB03` |
| Supervisor approval | `00000` | `escalation_pending` on the native `<dialog>` |

Every assertion resolved on the **`data-testid` rank** (see the
`assertion_matched` lines: `Css:[data-testid='waive-fee']`), which is the
point: on modern web the compiler ranks the author's published automation
contract above ids, the inverse of the legacy strategy where ids are all
there is and text/coordinates carry the fallback weight.
