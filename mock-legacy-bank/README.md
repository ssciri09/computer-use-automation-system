# FirstCore Banking Platform v4.2.1 — mock legacy target

A deliberately hostile stand-in for a bank back-office web application, built as
a discovery-and-replay target for a computer-use automation system. It reproduces
the things that actually break UI agents: nested iframes, auto-generated IDs,
`<span onclick>` controls, multi-second host latency, DOM that only appears after
a query resolves, and a security modal that escapes the frame it was triggered
from.

Six working modules — two read-only, three writes, one report — so a misrouted
agent lands on a real screen with a different input contract instead of a dead
link.

No dependencies. Python 3.8+.

## Run it

```bash
python3 server.py
```

Open <http://127.0.0.1:8080/>.

Sign-on: **`operator` / `letmein`**. Manager override PIN: **`7391`**.

```bash
PORT=9000 python3 server.py            # different port
LATENCY_SCALE=0.2 python3 server.py    # 5x faster, for dev loops
```

## Reach the target screen

1. Sign on.
2. Dismiss the "Notice to Authorized Users" modal.
3. In the left menu, click the **`+`** next to *Account Servicing*. Clicking the
   words does not expand it.
4. Click **Fee Management**. The workspace injects a nested iframe after ~1.2s.
5. Type an account identifier and click **Execute Inquiry**.
6. Click **Waive Fee** on the returned fee row.

Try `12345` first. Then see [SCENARIOS.md](SCENARIOS.md) for everything else.

## Modules

| Menu path | Module | Kind |
|---|---|---|
| Inquiry > Balance Inquiry | `BalInq` | read |
| Inquiry > Transaction History | `TxnHist` | read, paginated, has a `<select>` |
| Account Servicing > Stop Payments | `StopPay` | write, 2-step |
| Account Servicing > Fee Management | `FeeMgmt` | write, irreversible |
| Account Servicing > Address Maintenance | `AddrMaint` | write, 3-step form edit |
| Reports > Daily Fee Register | `FeeReg` | date-driven report |

All six are live. Every one has a success path, at least one expected business
outcome, and the writes have an escalation path.

## Verify it works

```bash
pip install playwright && python3 -m playwright install chromium
python3 smoke_test.py            # add HEADED=1 to watch
```

Expected:

22 `[ok]` lines covering every module, then:

```
passed: ALL_MODULES_REACHABLE, FEEMGMT_TAXONOMY, BALINQ, TXNHIST,
        STOPPAY, ADDRMAINT, FEEREG
```

`smoke_test.py` is a hand-written driver, not part of the app. It exists to prove
every scenario is reachable before you point an LLM at it — if your agent gets
stuck, run this first to confirm the app is behaving.

## Layout

```
server.py             routing, session, scenario logic, artificial latency
smoke_test.py         optional Playwright verification of all four scenarios
sample_artifact.json  example capability artifact recorded against this app
SCENARIOS.md          account-by-account behaviour and the obstacles list
app/
  Login.jsp           sign-on
  Main.jsp            frameset shell + disclaimer modal
  Hdr.jsp             header frame
  Nav.jsp             sidebar tree frame
  Wrk.jsp             workspace frame; injects the nested module iframe
  mod/BalInq.jsp      balance inquiry
  mod/TxnHist.jsp     transaction history (paginated)
  mod/StopPay.jsp     stop payments (validate -> commit)
  mod/FeeMgmt.jsp     fee waiver — the primary target screen
  mod/AddrMaint.jsp   address maintenance (load -> review -> commit)
  mod/FeeReg.jsp      daily fee register report
  legacy.css          ExtJS-era stylesheet
  ext-core.js         XHR helpers + the top-frame security interception
```

## Frame hierarchy

```
top  (Main.jsp)
 ├── hdrFrm   Hdr.jsp
 ├── navFrm   Nav.jsp
 └── wrkFrm   Wrk.jsp
      └── modFrm   mod/FeeMgmt.jsp     <- everything interesting lives here
```

The security modal for account `00000` is injected into **top**, not `modFrm`.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/portal/j_security_check` | sign-on, sets `JSESSIONID` |
| GET | `/portal/Main.do` | frameset shell |
| GET | `/portal/mod/<Module>.jsp` | module frame (~1.4s delay) |
| GET | `/portal/svc/AcctInq.do?acctId=` | fee inquiry — HTML fragment, not JSON |
| POST | `/portal/svc/WaiveFee.do` | JSON with the outcome class |
| GET | `/portal/svc/BalInq.do?acctId=` | balance inquiry |
| GET | `/portal/svc/TxnHist.do?acctId=&days=&page=` | paged transactions |
| POST | `/portal/svc/StopPayValidate.do` | stop payment step 1 |
| POST | `/portal/svc/StopPayCommit.do` | stop payment step 2 |
| GET | `/portal/svc/AddrLoad.do?acctId=` | address form |
| POST | `/portal/svc/AddrCommit.do` | address commit |
| GET | `/portal/svc/FeeReg.do?bizDate=` | fee register report |
| POST | `/portal/svc/Override.do` | manager PIN verification |
| GET | `/portal/Logout.do` | clears the session |

Session state is in-process. The `55555` retry counter is tracked per session
*and* per module, so each workflow exercises the recoverable path independently.
Restarting the server resets it.

## Using this as an assignment target

`sample_artifact.json` is a worked example of what a discovery run against this
app should compile down to. The parts worth copying into your own schema:

- **Frame paths are part of the locator**, not ambient state. A step says
  `"frame": {"path": ["wrkFrm","modFrm"]}` so replay never has to guess.
- **Locators are ranked chains** — `id → name → text → coordinates` — because the
  `ext-genNN` IDs are exactly the kind that churn between vendor releases.
- **Outcome assertions are declared at record time.** "Account is closed" is a
  business outcome only because the artifact says so. Inferring it during replay
  is guessing.
- **The escalation assertion names the frame it checks** (`"frame": "top"`) and
  the step to resume at, which is what makes the `00000` handoff recoverable
  rather than a dead run.

## Safety note

Everything here is fabricated. Names, balances, and fee references are invented,
the "PII" is meaningless, and the server binds to `127.0.0.1` by default. There
is no real data to leak — but keep the redaction path exercised anyway, since the
whole point is to prove the pipeline scrubs before it persists.
