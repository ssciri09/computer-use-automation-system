#!/usr/bin/env python3
"""
FirstCore Banking Platform v4.2.1 (build 20081114)
Mock legacy web application for computer-use automation testing.

Zero dependencies. Python 3.8+.

    python3 server.py

Then open http://127.0.0.1:8080/

Environment:
    PORT            default 8080
    LATENCY_SCALE   default 1.0  (set to 0.2 to run scenarios fast during dev)
"""

import http.server
import json
import mimetypes
import os
import socketserver
import time
import urllib.parse
import uuid

HOST = os.environ.get("HOST", "127.0.0.1")
PORT = int(os.environ.get("PORT", "8080"))
SCALE = float(os.environ.get("LATENCY_SCALE", "1.0"))

BASE = os.path.dirname(os.path.abspath(__file__))
APPDIR = os.path.join(BASE, "app")

CREDS = {"operator": "letmein"}
OVERRIDE_PIN = "7391"
MODULES = ["FeeMgmt", "BalInq", "TxnHist", "StopPay", "AddrMaint", "FeeReg"]

SESSIONS = {}

ACCOUNTS = {
    "12345": {
        "name": "M R HOLLOWAY", "status": "ACTIVE", "branch": "014",
        "product": "REG CHECKING", "balance": "1,284.09",
        "avail": "1,184.09", "hold": "100.00", "stmt": "08/01/2026",
        "fee_id": "FEE-88213", "fee_type": "OVERDRAFT ITEM FEE",
        "fee_amt": "35.00", "fee_date": "08/26/2026",
    },
    "55555": {
        "name": "D P ANANTHAKRISHNAN", "status": "ACTIVE", "branch": "022",
        "product": "PREMIER CHECKING", "balance": "412.55",
        "avail": "412.55", "hold": "0.00", "stmt": "08/01/2026",
        "fee_id": "FEE-88401", "fee_type": "OVERDRAFT ITEM FEE",
        "fee_amt": "35.00", "fee_date": "08/25/2026",
    },
    "99999": {
        "name": "T L OKONKWO", "status": "CLOSED", "branch": "007",
        "product": "REG CHECKING", "balance": "0.00",
        "avail": "0.00", "hold": "0.00", "stmt": "07/01/2026",
        "fee_id": "FEE-71190", "fee_type": "OVERDRAFT ITEM FEE",
        "fee_amt": "35.00", "fee_date": "07/02/2026",
    },
    "00000": {
        "name": "RESTRICTED PARTY", "status": "ACTIVE", "branch": "001",
        "product": "REG CHECKING", "balance": "9,430.11",
        "avail": "9,430.11", "hold": "0.00", "stmt": "08/01/2026",
        "fee_id": "FEE-90002", "fee_type": "OVERDRAFT ITEM FEE",
        "fee_amt": "35.00", "fee_date": "08/26/2026",
    },
}

ADDRESSES = {
    "12345": {"l1": "1418 W PECAN ST", "l2": "APT 3B", "city": "GEORGETOWN",
              "st": "TX", "zip": "78626"},
    "55555": {"l1": "9902 SPICEWOOD SPRINGS RD", "l2": "", "city": "AUSTIN",
              "st": "TX", "zip": "78759"},
    "99999": {"l1": "77 LAKEVIEW TER", "l2": "", "city": "ROUND ROCK",
              "st": "TX", "zip": "78664"},
    "00000": {"l1": "1 COMMERCE PLZ", "l2": "STE 400", "city": "DALLAS",
              "st": "TX", "zip": "75201"},
}

TXNS = {
    "12345": [
        ("08/26/2026", "OVERDRAFT ITEM FEE", "FEE", "-35.00", "1,284.09"),
        ("08/25/2026", "POS PURCHASE 4471 HEB #412", "DR", "-88.14", "1,319.09"),
        ("08/24/2026", "ACH DEP PAYROLL WESTLAKE", "CR", "2,105.60", "1,407.23"),
        ("08/22/2026", "ATM WDL BR014 TERM 22", "DR", "-200.00", "-698.37"),
        ("08/21/2026", "CHECK 001042", "DR", "-412.00", "-498.37"),
        ("08/19/2026", "POS PURCHASE 1180 SHELL", "DR", "-61.22", "-86.37"),
        ("08/18/2026", "TRANSFER TO SAV 8891", "DR", "-500.00", "-25.15"),
        ("08/15/2026", "ACH DEP PAYROLL WESTLAKE", "CR", "2,105.60", "474.85"),
    ],
    "55555": [
        ("08/25/2026", "OVERDRAFT ITEM FEE", "FEE", "-35.00", "412.55"),
        ("08/24/2026", "POS PURCHASE 9920 COSTCO", "DR", "-241.88", "447.55"),
        ("08/20/2026", "ACH DEP PAYROLL NORTHSTAR", "CR", "1,890.00", "689.43"),
    ],
    "99999": [],
    "00000": [
        ("08/26/2026", "OVERDRAFT ITEM FEE", "FEE", "-35.00", "9,430.11"),
        ("08/20/2026", "WIRE IN REF 88213004", "CR", "9,000.00", "9,465.11"),
    ],
}

FEE_REGISTER = {
    "08/26/2026": [
        ("12345", "M R HOLLOWAY", "FEE-88213", "OVERDRAFT ITEM FEE", "35.00", "014"),
        ("00000", "RESTRICTED PARTY", "FEE-90002", "OVERDRAFT ITEM FEE", "35.00", "001"),
        ("40219", "S J BRENNEMAN", "FEE-88220", "NSF RETURN FEE", "30.00", "014"),
    ],
    "08/25/2026": [
        ("55555", "D P ANANTHAKRISHNAN", "FEE-88401", "OVERDRAFT ITEM FEE", "35.00", "022"),
    ],
}

PAGE_SIZE = 5


def nap(seconds):
    time.sleep(max(0.0, seconds * SCALE))


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "Netscape-Enterprise/4.1"
    sys_version = ""

    def log_message(self, fmt, *args):
        print("[app] %s - %s" % (self.address_string(), fmt % args))

    # ---------- plumbing ----------

    def sid(self):
        raw = self.headers.get("Cookie", "")
        for part in raw.split(";"):
            k, _, v = part.strip().partition("=")
            if k == "JSESSIONID" and v in SESSIONS:
                return v
        return None

    def send(self, body, ctype="text/html; charset=utf-8", code=200, headers=None):
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-cache, no-store")
        self.send_header("X-Powered-By", "FirstCore/4.2.1")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def send_json(self, obj, code=200):
        self.send(json.dumps(obj), "application/json", code)

    def redirect(self, loc, headers=None):
        h = {"Location": loc}
        h.update(headers or {})
        self.send("", "text/html", 302, h)

    def serve_file(self, relpath):
        path = os.path.normpath(os.path.join(APPDIR, relpath))
        if not path.startswith(APPDIR) or not os.path.isfile(path):
            self.send("<h1>404 Not Found</h1>", code=404)
            return
        ctype, _ = mimetypes.guess_type(path)
        if path.endswith(".jsp"):
            ctype = "text/html; charset=utf-8"
        with open(path, "rb") as fh:
            self.send(fh.read(), ctype or "application/octet-stream")

    def body_params(self):
        n = int(self.headers.get("Content-Length", "0") or 0)
        raw = self.rfile.read(n).decode("utf-8") if n else ""
        return {k: v[0] for k, v in urllib.parse.parse_qs(raw).items()}

    def transient(self, key, acct, lag=6.0, limit=3):
        """55555 congests the host link for the first `limit-1` attempts.
        Counter is per session AND per module, so each workflow tests it."""
        if acct != "55555":
            return False
        sess = SESSIONS[self.sid()]
        k = "%s:%s" % (key, acct)
        n = sess["attempts"].get(k, 0) + 1
        sess["attempts"][k] = n
        if n < limit:
            nap(lag)
            return True
        return False

    HOST_BUSY = ('<div class="x-msg-warn" id="ext-comp-1099">'
                 'HOST-0521: Legacy host link congested. Resubmit inquiry.</div>')

    # ---------- routes ----------

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        route = parsed.path
        qs = {k: v[0] for k, v in urllib.parse.parse_qs(parsed.query).items()}

        if route == "/":
            return self.redirect("/portal/Login.jsp" if not self.sid() else "/portal/Main.do")
        if route == "/portal/Login.jsp":
            return self.serve_file("Login.jsp")
        if route in ("/legacy.css", "/ext-core.js"):
            return self.serve_file(route.lstrip("/"))

        if route.startswith("/portal/"):
            if not self.sid():
                return self.redirect("/portal/Login.jsp")

            if route == "/portal/Main.do":
                return self.serve_file("Main.jsp")
            if route in ("/portal/Hdr.jsp", "/portal/Nav.jsp", "/portal/Wrk.jsp"):
                return self.serve_file(route.split("/")[-1])
            if route.startswith("/portal/mod/") and route.endswith(".jsp"):
                mod = route[len("/portal/mod/"):-4]
                if mod in MODULES:
                    nap(1.4)
                    return self.serve_file(os.path.join("mod", mod + ".jsp"))
                return self.send("<h1>404 Not Found</h1>", code=404)

            if route == "/portal/svc/AcctInq.do":
                return self.acct_inq(qs)
            if route == "/portal/svc/BalInq.do":
                return self.bal_inq(qs)
            if route == "/portal/svc/TxnHist.do":
                return self.txn_hist(qs)
            if route == "/portal/svc/AddrLoad.do":
                return self.addr_load(qs)
            if route == "/portal/svc/FeeReg.do":
                return self.fee_reg(qs)
            if route == "/portal/Logout.do":
                SESSIONS.pop(self.sid(), None)
                return self.redirect("/portal/Login.jsp",
                                     {"Set-Cookie": "JSESSIONID=; Max-Age=0; Path=/"})

        self.send("<h1>404 Not Found</h1>", code=404)

    def do_POST(self):
        route = urllib.parse.urlparse(self.path).path
        p = self.body_params()

        if route == "/portal/j_security_check":
            nap(0.8)
            if CREDS.get(p.get("j_username", "")) == p.get("j_password", ""):
                sid = uuid.uuid4().hex.upper()
                SESSIONS[sid] = {"attempts": {}}
                return self.redirect("/portal/Main.do",
                                     {"Set-Cookie": "JSESSIONID=%s; Path=/" % sid})
            return self.send(
                open(os.path.join(APPDIR, "Login.jsp"), encoding="utf-8").read()
                .replace("<!--ERRSLOT-->",
                         '<div class="x-form-invalid-msg" id="ext-gen23">'
                         'Sign-on failed. Verify user id and password.</div>'))

        if not self.sid():
            return self.send_json({"result": "SESSION_EXPIRED"}, 401)

        if route == "/portal/svc/WaiveFee.do":
            return self.waive_fee(p)
        if route == "/portal/svc/StopPayValidate.do":
            return self.stop_validate(p)
        if route == "/portal/svc/StopPayCommit.do":
            return self.stop_commit(p)
        if route == "/portal/svc/AddrCommit.do":
            return self.addr_commit(p)
        if route == "/portal/svc/Override.do":
            return self.override(p)

        self.send("<h1>404 Not Found</h1>", code=404)

    # ---------- shared render helpers ----------

    def hdr(self, gid, text):
        return ('<div class="x-panel-header" id="%s" style="margin-top:10px">%s</div>'
                % (gid, text))

    def grid(self, gid, cols, rows, rowids=None, aligns=None):
        aligns = aligns or [""] * len(cols)
        out = ['<table cellpadding="0" cellspacing="0" border="0" class="x-grid3" id="%s">' % gid,
               '<tr class="x-grid3-hd-row">']
        for c in cols:
            out.append('<td class="x-grid3-hd-inner">%s</td>' % c)
        out.append("</tr>")
        for i, r in enumerate(rows):
            rid = ' id="%s"' % rowids[i] if rowids and i < len(rowids) else ""
            out.append('<tr class="x-grid3-row"%s>' % rid)
            for j, cell in enumerate(r):
                a = ' align="right"' if aligns[j] == "r" else ""
                out.append('<td class="x-grid-cell-inner"%s>%s</td>' % (a, cell))
            out.append("</tr>")
        out.append("</table>")
        return "".join(out)

    def btn(self, gid, label, onclick):
        return ('<table cellpadding="0" cellspacing="0" border="0" class="x-btn"><tr>'
                '<td class="x-btn-ml"><i>&nbsp;</i></td>'
                '<td class="x-btn-mc"><span class="x-btn-text" id="%s" onclick="%s">%s</span></td>'
                '<td class="x-btn-mr"><i>&nbsp;</i></td></tr></table>'
                % (gid, onclick, label))

    def warn(self, gid, msg):
        return '<div class="x-msg-warn" id="%s">%s</div>' % (gid, msg)

    def no_records(self):
        return self.warn("ext-comp-1098",
                         "No records found for the supplied account identifier.")

    # ---------- Fee Management ----------

    def acct_inq(self, qs):
        acct = (qs.get("acctId") or "").strip()
        if self.transient("feeinq", acct):
            return self.send(self.HOST_BUSY)
        nap(3.4)
        rec = ACCOUNTS.get(acct)
        if not rec:
            return self.send(self.no_records())
        body = (
            '<div class="x-panel-header" id="ext-gen61">Account Detail &mdash; Inquiry Result</div>'
            + self.grid("ext-gen62",
                        ["ACCT", "NAME", "BR", "PRODUCT", "STATUS", "LEDGER BAL"],
                        [[acct, rec["name"], rec["branch"], rec["product"],
                          rec["status"], rec["balance"]]],
                        aligns=["", "", "", "", "", "r"])
            + self.hdr("ext-gen70", "Assessed Fees")
            + self.grid("ext-gen71",
                        ["FEE REF", "DESCRIPTION", "POSTED", "AMT", "&nbsp;"],
                        [[rec["fee_id"], rec["fee_type"], rec["fee_date"], rec["fee_amt"],
                          self.btn("ext-gen77", "Waive Fee",
                                   "doWaive('%s','%s')" % (acct, rec["fee_id"]))]],
                        rowids=["ext-gen72"], aligns=["", "", "", "r", ""])
            + '<div id="ext-comp-1102"></div>')
        return self.send(body)

    def waive_fee(self, p):
        acct = (p.get("acctId") or "").strip()
        nap(2.2)
        rec = ACCOUNTS.get(acct)
        if not rec:
            return self.send_json({"result": "NOT_FOUND"})
        if acct == "99999":
            return self.send_json({"result": "BUSINESS_REJECT",
                                   "msg": "Error: Account is closed. Fee waiver not permitted."})
        if acct == "00000":
            return self.send_json({"result": "SECURITY_INTERCEPT",
                                   "msg": "SECURITY INTERCEPTION: Manager Override PIN Required"})
        return self.send_json({
            "result": "OK",
            "msg": "Fee %s reversed. Credit of %s posted to account %s."
                   % (rec["fee_id"], rec["fee_amt"], acct),
            "conf": "RVSL-" + uuid.uuid4().hex[:8].upper()})

    # ---------- Balance Inquiry ----------

    def bal_inq(self, qs):
        acct = (qs.get("acctId") or "").strip()
        if self.transient("balinq", acct):
            return self.send(self.HOST_BUSY)
        nap(3.1)
        rec = ACCOUNTS.get(acct)
        if not rec:
            return self.send(self.no_records())

        note = ""
        led, avl = rec["balance"], rec["avail"]
        if acct == "00000":
            led = avl = "*** RESTRICTED ***"
            note = self.warn("ext-comp-2015",
                             "Balance detail suppressed by information barrier. "
                             "Contact Compliance for disclosure.")
        elif rec["status"] == "CLOSED":
            note = self.warn("ext-comp-2016",
                             "Account is closed. Balances shown are as of closure date.")

        body = (
            '<div class="x-panel-header" id="ext-gen56">Balance Summary</div>'
            + self.grid("ext-gen57",
                        ["ACCT", "NAME", "PRODUCT", "STATUS", "LAST STMT"],
                        [[acct, rec["name"], rec["product"], rec["status"], rec["stmt"]]])
            + self.hdr("ext-gen58", "Position")
            + self.grid("ext-gen59",
                        ["LEDGER BAL", "AVAILABLE BAL", "HOLD AMT"],
                        [[led, avl, rec["hold"]]],
                        rowids=["ext-gen59r"], aligns=["r", "r", "r"])
            + note)
        return self.send(body)

    # ---------- Transaction History ----------

    def txn_hist(self, qs):
        acct = (qs.get("acctId") or "").strip()
        page = max(1, int(qs.get("page") or "1"))
        days = qs.get("days") or "30"
        if self.transient("txnhist", acct):
            return self.send(self.HOST_BUSY)
        nap(3.6)
        if acct not in ACCOUNTS:
            return self.send(self.no_records())

        rows = TXNS.get(acct, [])
        if not rows:
            return self.send(self.warn("ext-comp-2020",
                                       "No activity in the selected period."))

        start = (page - 1) * PAGE_SIZE
        chunk = rows[start:start + PAGE_SIZE]
        if not chunk:
            return self.send(self.warn("ext-comp-2021", "End of statement reached."))

        pager = '<div style="margin-top:6px" id="ext-gen68">Page %d of %d &nbsp;' % (
            page, (len(rows) + PAGE_SIZE - 1) // PAGE_SIZE)
        if start + PAGE_SIZE < len(rows):
            pager += ('<span class="x-btn-text" id="ext-gen66" '
                      'onclick="goPage(%d)">Next Page &raquo;</span>' % (page + 1))
        if page > 1:
            pager += ('&nbsp;&nbsp;<span class="x-btn-text" id="ext-gen67" '
                      'onclick="goPage(%d)">&laquo; Prior Page</span>' % (page - 1))
        pager += "</div>"

        body = ('<div class="x-panel-header" id="ext-gen64">Transaction History &mdash; '
                'last %s days</div>' % days
                + self.grid("ext-gen65",
                            ["POST DATE", "DESCRIPTION", "TYPE", "AMOUNT", "RUN BAL"],
                            [list(r) for r in chunk],
                            aligns=["", "", "", "r", "r"])
                + pager)
        return self.send(body)

    # ---------- Stop Payments ----------

    def stop_validate(self, p):
        acct = (p.get("acctId") or "").strip()
        chk = (p.get("chkNbr") or "").strip()
        if self.transient("stoppay", acct):
            return self.send_json({"result": "TRANSIENT",
                                   "msg": "HOST-0521: Legacy host link congested. Resubmit inquiry."})
        nap(3.0)
        rec = ACCOUNTS.get(acct)
        if not rec:
            return self.send_json({"result": "NOT_FOUND",
                                   "msg": "No records found for the supplied account identifier."})
        if chk == "000000":
            return self.send_json({"result": "BUSINESS_REJECT",
                                   "msg": "Error: Check has already cleared. Stop payment not permitted."})
        if rec["status"] == "CLOSED":
            return self.send_json({"result": "BUSINESS_REJECT",
                                   "msg": "Error: Account is closed. Stop payment cannot be placed."})
        return self.send_json({
            "result": "OK", "acct": acct, "name": rec["name"], "chk": chk,
            "fee": "32.00",
            "msg": "Item located. Review and confirm to place the stop payment."})

    def stop_commit(self, p):
        acct = (p.get("acctId") or "").strip()
        chk = (p.get("chkNbr") or "").strip()
        nap(2.4)
        if acct == "00000":
            return self.send_json({"result": "SECURITY_INTERCEPT",
                                   "msg": "SECURITY INTERCEPTION: Manager Override PIN Required"})
        return self.send_json({
            "result": "OK",
            "msg": "Stop payment placed on check %s for account %s. Service fee 32.00 assessed."
                   % (chk, acct),
            "conf": "SP-" + uuid.uuid4().hex[:8].upper()})

    # ---------- Address Maintenance ----------

    def addr_load(self, qs):
        acct = (qs.get("acctId") or "").strip()
        if self.transient("addr", acct):
            return self.send(self.HOST_BUSY)
        nap(3.0)
        rec = ACCOUNTS.get(acct)
        a = ADDRESSES.get(acct)
        if not rec or not a:
            return self.send(self.no_records())

        def fld(gid, label, val, size, maxlen):
            return ('<tr><td align="right">%s:</td><td><input type="text" id="%s" '
                    'class="x-form-field" size="%d" maxlength="%d" value="%s"></td></tr>'
                    % (label, gid, size, maxlen, val))

        body = (
            '<div class="x-panel-header" id="ext-gen84">Mailing Address &mdash; %s (%s)</div>'
            % (rec["name"], acct)
            + '<table cellpadding="2" cellspacing="0" border="0" id="ext-gen85">'
            + fld("ext-gen86_l1", "Address Line 1", a["l1"], 34, 40)
            + fld("ext-gen86_l2", "Address Line 2", a["l2"], 34, 40)
            + fld("ext-gen86_ct", "City", a["city"], 24, 30)
            + fld("ext-gen86_st", "State", a["st"], 4, 2)
            + fld("ext-gen86_zp", "ZIP", a["zip"], 10, 10)
            + "</table>"
            + '<div style="margin-top:8px">'
            + self.btn("ext-gen87", "Review Changes", "doReview('%s')" % acct)
            + "</div><div id=\"ext-comp-2030\"></div>")
        return self.send(body)

    def addr_commit(self, p):
        acct = (p.get("acctId") or "").strip()
        nap(2.4)
        rec = ACCOUNTS.get(acct)
        if not rec:
            return self.send_json({"result": "NOT_FOUND"})
        if rec["status"] == "CLOSED":
            return self.send_json({"result": "BUSINESS_REJECT",
                                   "msg": "Error: Account is closed. Maintenance not permitted."})
        if acct == "00000":
            return self.send_json({"result": "SECURITY_INTERCEPT",
                                   "msg": "SECURITY INTERCEPTION: Manager Override PIN Required"})
        if not (p.get("st") or "").strip() or len((p.get("zp") or "").strip()) < 5:
            return self.send_json({"result": "BUSINESS_REJECT",
                                   "msg": "Error: State and ZIP are required and must be valid."})
        return self.send_json({
            "result": "OK",
            "msg": "Address updated on account %s. Change effective next cycle." % acct,
            "conf": "ADR-" + uuid.uuid4().hex[:8].upper()})

    # ---------- Daily Fee Register ----------

    def fee_reg(self, qs):
        d = (qs.get("bizDate") or "").strip()
        nap(5.0)
        if not d:
            return self.send(self.warn("ext-comp-2040",
                                       "Business date is required."))
        rows = FEE_REGISTER.get(d)
        if not rows:
            return self.send(self.warn("ext-comp-2041",
                                       "No fees assessed for the selected business date."))
        total = sum(float(r[4]) for r in rows)
        body = ('<div class="x-panel-header" id="ext-gen97">Daily Fee Register &mdash; %s</div>' % d
                + self.grid("ext-gen98",
                            ["ACCT", "NAME", "FEE REF", "DESCRIPTION", "AMT", "BR"],
                            [list(r) for r in rows],
                            aligns=["", "", "", "", "r", ""])
                + '<div style="margin-top:6px;font-weight:bold" id="ext-gen99_tot">'
                  'Items: %d &nbsp;&nbsp; Total assessed: %.2f</div>' % (len(rows), total))
        return self.send(body)

    # ---------- override ----------

    def override(self, p):
        nap(1.0)
        if p.get("pin") != OVERRIDE_PIN:
            return self.send_json({"result": "DENIED", "msg": "Override PIN rejected."})
        acct = (p.get("acctId") or "").strip()
        rec = ACCOUNTS.get(acct, {})
        ctx = p.get("ctx") or "fee"
        msgs = {
            "fee": "Override accepted. Fee %s reversed on account %s." % (rec.get("fee_id", "?"), acct),
            "stop": "Override accepted. Stop payment placed on account %s." % acct,
            "addr": "Override accepted. Address updated on account %s." % acct,
        }
        pfx = {"fee": "RVSL-", "stop": "SP-", "addr": "ADR-"}
        return self.send_json({"result": "OK", "msg": msgs.get(ctx, msgs["fee"]),
                               "conf": pfx.get(ctx, "RVSL-") + uuid.uuid4().hex[:8].upper()})


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    print("FirstCore Banking Platform v4.2.1  (mock legacy target)")
    print("  http://%s:%d/" % (HOST, PORT))
    print("  sign-on: operator / letmein     override PIN: %s" % OVERRIDE_PIN)
    print("  latency scale: %.2fx" % SCALE)
    try:
        Server((HOST, PORT), Handler).serve_forever()
    except KeyboardInterrupt:
        print("\nshutdown")
