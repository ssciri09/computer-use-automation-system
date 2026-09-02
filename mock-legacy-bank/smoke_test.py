#!/usr/bin/env python3
"""
Smoke test for the mock legacy app. Optional -- proves the frame hierarchy,
the async waits, and every module's scenario outcomes are reachable.

    pip install playwright && python3 -m playwright install chromium
    python3 smoke_test.py                # headless
    HEADED=1 python3 smoke_test.py       # watch it

Assumes the server is already running on PORT (default 8080).
"""

import os
import sys

from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:%s" % os.environ.get("PORT", "8080")
HEADED = os.environ.get("HEADED") == "1"

# nav leaf id -> (folder glyph id, a selector that proves the module loaded)
MODULES = {
    "BalInq":    ("ext-gen81", "ext-gen82", "#ext-gen52_input"),
    "TxnHist":   ("ext-gen81", "ext-gen83", "#ext-gen60_input"),
    "StopPay":   ("ext-gen89", "ext-gen90", "#ext-gen70_input"),
    "FeeMgmt":   ("ext-gen89", "ext-gen91", "#ext-gen42_input"),
    "AddrMaint": ("ext-gen89", "ext-gen92", "#ext-gen80_input"),
    "FeeReg":    ("ext-gen94", "ext-gen96", "#ext-gen92_input"),
}


def module_frame(page):
    """Descend top -> wrkFrm -> modFrm."""
    wrk = page.frame(name="wrkFrm")
    assert wrk, "workspace frame missing"
    assert wrk.child_frames, "module frame not yet injected"
    return wrk.child_frames[0]


def open_module(page, name):
    glyph, leaf, probe = MODULES[name]
    page.goto(BASE + "/")
    if page.query_selector("#ext-gen17_u"):          # not yet signed on
        page.fill("#ext-gen17_u", "operator")
        page.fill("#ext-gen19_p", "letmein")
        page.click("#ext-gen21")
    page.wait_for_selector("#ext-comp-1041")
    page.click("#ext-gen33")                         # dismiss disclaimer
    nav = page.frame(name="navFrm")
    nav.wait_for_selector("#" + glyph)
    nav.click("#" + glyph)                           # expand via glyph, not label
    nav.click("#" + leaf)
    page.wait_for_timeout(2000)                      # workspace injects nested iframe
    mod = module_frame(page)
    mod.wait_for_selector(probe, timeout=20000)
    return mod


def settle(frame, timeout=45000):
    frame.wait_for_function("!document.getElementById('loading-spinner')", timeout=timeout)


def run(page):
    ok = []

    # ---- every module reachable ----
    for name in MODULES:
        open_module(page, name)
        print("[ok] %-9s module loaded through 2 iframe levels" % name)
    ok.append("ALL_MODULES_REACHABLE")

    # ---- Fee Management: all four classifications ----
    mod = open_module(page, "FeeMgmt")
    mod.fill("#ext-gen42_input", "12345"); mod.click("#ext-gen44"); settle(mod)
    mod.click("#ext-gen77"); mod.wait_for_selector("#ext-gen110", timeout=30000)
    print("[ok] FeeMgmt   12345 -> success")

    mod = open_module(page, "FeeMgmt")
    for _ in range(5):
        mod.fill("#ext-gen42_input", "55555"); mod.click("#ext-gen44"); settle(mod, 60000)
        if "HOST-0521" not in mod.inner_html("#ext-comp-1097"):
            break
    assert "ANANTHAKRISHNAN" in mod.inner_html("#ext-comp-1097")
    print("[ok] FeeMgmt   55555 -> recoverable, resolved on retry")

    mod = open_module(page, "FeeMgmt")
    mod.fill("#ext-gen42_input", "99999"); mod.click("#ext-gen44"); settle(mod)
    mod.click("#ext-gen77"); mod.wait_for_selector("#ext-gen111", timeout=30000)
    assert "Account is closed" in mod.inner_text("#ext-gen111")
    print("[ok] FeeMgmt   99999 -> business outcome")

    mod = open_module(page, "FeeMgmt")
    mod.fill("#ext-gen42_input", "00000"); mod.click("#ext-gen44"); settle(mod)
    mod.click("#ext-gen77")
    page.wait_for_selector("#secOvl", timeout=30000)      # TOP frame, not mod
    page.fill("#ext-gen99_pin", "7391"); page.click("#ext-gen101")
    mod.wait_for_selector("#ext-gen110", timeout=30000)
    print("[ok] FeeMgmt   00000 -> escalated, cleared, resumed")
    ok.append("FEEMGMT_TAXONOMY")

    # ---- Balance Inquiry ----
    mod = open_module(page, "BalInq")
    mod.fill("#ext-gen52_input", "12345"); mod.click("#ext-gen54"); settle(mod)
    assert "1,184.09" in mod.inner_html("#ext-gen59")
    print("[ok] BalInq    12345 -> available balance read")
    mod.fill("#ext-gen52_input", "00000"); mod.click("#ext-gen54"); settle(mod)
    assert mod.query_selector("#ext-comp-2015"), "information barrier not shown"
    print("[ok] BalInq    00000 -> balances suppressed (business outcome)")
    mod.fill("#ext-gen52_input", "77777"); mod.click("#ext-gen54"); settle(mod)
    assert mod.query_selector("#ext-comp-1098")
    print("[ok] BalInq    77777 -> no records (business outcome)")
    ok.append("BALINQ")

    # ---- Transaction History ----
    mod = open_module(page, "TxnHist")
    mod.fill("#ext-gen60_input", "12345")
    mod.select_option("#ext-gen61_sel", "90")
    mod.click("#ext-gen62b"); settle(mod)
    assert mod.query_selector("#ext-gen66"), "pager missing"
    mod.click("#ext-gen66"); settle(mod)
    assert "Page 2 of 2" in mod.inner_text("#ext-gen68")
    print("[ok] TxnHist   12345 -> paged to page 2 of 2")
    mod.fill("#ext-gen60_input", "99999"); mod.click("#ext-gen62b"); settle(mod)
    assert mod.query_selector("#ext-comp-2020")
    print("[ok] TxnHist   99999 -> no activity (business outcome)")
    ok.append("TXNHIST")

    # ---- Stop Payments ----
    mod = open_module(page, "StopPay")
    mod.fill("#ext-gen70_input", "12345"); mod.fill("#ext-gen71_input", "001055")
    mod.click("#ext-gen73"); settle(mod)
    mod.click("#ext-gen75"); mod.wait_for_selector("#ext-gen79", timeout=30000)
    print("[ok] StopPay   12345 -> success")
    mod = open_module(page, "StopPay")
    mod.fill("#ext-gen70_input", "12345"); mod.fill("#ext-gen71_input", "000000")
    mod.click("#ext-gen73"); settle(mod)
    assert "already cleared" in mod.inner_text("#ext-gen78")
    print("[ok] StopPay   check 000000 -> already cleared (business outcome)")
    mod = open_module(page, "StopPay")
    mod.fill("#ext-gen70_input", "00000"); mod.fill("#ext-gen71_input", "001099")
    mod.click("#ext-gen73"); settle(mod)
    mod.click("#ext-gen75")
    page.wait_for_selector("#secOvl", timeout=30000)
    page.fill("#ext-gen99_pin", "7391"); page.click("#ext-gen101")
    mod.wait_for_selector("#ext-gen79", timeout=30000)
    print("[ok] StopPay   00000 -> escalated, cleared, resumed")
    ok.append("STOPPAY")

    # ---- Address Maintenance ----
    mod = open_module(page, "AddrMaint")
    mod.fill("#ext-gen80_input", "12345"); mod.click("#ext-gen82b"); settle(mod)
    mod.fill("#ext-gen86_l1", "220 E 8TH ST")
    mod.click("#ext-gen87")
    mod.wait_for_selector("#ext-gen89c", timeout=10000)
    mod.click("#ext-gen89c"); mod.wait_for_selector("#ext-gen90a", timeout=30000)
    print("[ok] AddrMaint 12345 -> load, edit, review, commit")
    mod = open_module(page, "AddrMaint")
    mod.fill("#ext-gen80_input", "99999"); mod.click("#ext-gen82b"); settle(mod)
    mod.click("#ext-gen87"); mod.wait_for_selector("#ext-gen89c", timeout=10000)
    mod.click("#ext-gen89c"); mod.wait_for_selector("#ext-gen91a", timeout=30000)
    assert "Account is closed" in mod.inner_text("#ext-gen91a")
    print("[ok] AddrMaint 99999 -> business outcome")
    ok.append("ADDRMAINT")

    # ---- Daily Fee Register ----
    mod = open_module(page, "FeeReg")
    mod.fill("#ext-gen92_input", "08/26/2026"); mod.click("#ext-gen94b"); settle(mod, 60000)
    assert "Items: 3" in mod.inner_text("#ext-gen99_tot")
    print("[ok] FeeReg    08/26/2026 -> 3 items")
    mod.fill("#ext-gen92_input", "01/01/2020"); mod.click("#ext-gen94b"); settle(mod, 60000)
    assert mod.query_selector("#ext-comp-2041")
    print("[ok] FeeReg    01/01/2020 -> no fees (business outcome)")
    ok.append("FEEREG")

    return ok


if __name__ == "__main__":
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=not HEADED)
        page = browser.new_page()
        try:
            res = run(page)
        finally:
            browser.close()
    print("\npassed: " + ", ".join(res))
    sys.exit(0)
