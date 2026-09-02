/* FirstCore ext-core.js  rev 1.1.9  -- do not edit, generated */

function xhrPost(url, params, cb) {
  var x = new XMLHttpRequest();
  x.open("POST", url, true);
  x.setRequestHeader("Content-Type", "application/x-www-form-urlencoded");
  x.onreadystatechange = function () {
    if (x.readyState === 4) {
      try { cb(JSON.parse(x.responseText)); }
      catch (e) { cb({ result: "PARSE_ERROR", msg: x.responseText }); }
    }
  };
  x.send(params);
}

function xhrGet(url, cb) {
  var x = new XMLHttpRequest();
  x.open("GET", url, true);
  x.onreadystatechange = function () {
    if (x.readyState === 4) { cb(x.responseText); }
  };
  x.send(null);
}

/*
 * Security interception. Deliberately escapes the module iframe and paints
 * over the top-level document, so an agent scoped to the inner frame loses
 * its execution context entirely.
 */
function __sec_intercept(acctId, msg, ctx) {
  ctx = ctx || "fee";
  var top = window.top.document;
  if (top.getElementById("secOvl")) { return; }

  var mask = top.createElement("div");
  mask.className = "x-mask";
  mask.id = "secMask";
  top.body.appendChild(mask);

  var w = top.createElement("div");
  w.className = "x-window";
  w.id = "secOvl";
  w.style.left = "50%";
  w.style.top = "180px";
  w.style.marginLeft = "-190px";
  w.innerHTML =
    '<div class="x-window-hd" id="ext-gen95">' + msg + '</div>' +
    '<div class="x-window-bd">' +
      '<div style="margin-bottom:8px">Account <b>' + acctId + '</b> carries a ' +
      'supervisory restriction. A manager override PIN is required to ' +
      'continue this transaction.</div>' +
      '<div style="margin-bottom:8px">' +
        'Override PIN: <input type="password" maxlength="4" size="8" ' +
        'class="x-form-field" id="ext-gen99_pin">' +
      '</div>' +
      '<div id="ext-gen100_err" class="x-form-invalid-msg"></div>' +
      '<table cellpadding="0" cellspacing="0" border="0" class="x-btn"><tr>' +
        '<td class="x-btn-mc"><span class="x-btn-text" id="ext-gen101" ' +
        'onclick="__sec_submit(\'' + acctId + '\',\'' + ctx + '\')">Authorize</span></td>' +
      '</tr></table>' +
    '</div>';
  top.body.appendChild(w);
  var f = top.getElementById("ext-gen99_pin");
  if (f) { f.focus(); }
}

function __sec_submit(acctId, ctx) {
  var top = window.top.document;
  var pin = top.getElementById("ext-gen99_pin").value;
  var err = top.getElementById("ext-gen100_err");
  err.innerHTML = "Verifying...";
  xhrPost("/portal/svc/Override.do",
    "acctId=" + encodeURIComponent(acctId) + "&pin=" + encodeURIComponent(pin) +
    "&ctx=" + encodeURIComponent(ctx || "fee"),
    function (r) {
      if (r.result === "OK") {
        var ovl = top.getElementById("secOvl");
        var msk = top.getElementById("secMask");
        if (ovl) { ovl.parentNode.removeChild(ovl); }
        if (msk) { msk.parentNode.removeChild(msk); }
        __sec_release(r);
      } else {
        err.innerHTML = r.msg || "Override rejected.";
      }
    });
}

/* Hands control back to whichever module frame raised the interception. */
function __sec_release(r) {
  var frames = window.top.frames;
  for (var i = 0; i < frames.length; i++) {
    try {
      if (frames[i].__onOverrideCleared) { frames[i].__onOverrideCleared(r); return; }
      var inner = frames[i].frames;
      for (var j = 0; j < inner.length; j++) {
        if (inner[j].__onOverrideCleared) { inner[j].__onOverrideCleared(r); return; }
      }
    } catch (e) { /* cross-frame access, ignore */ }
  }
}
