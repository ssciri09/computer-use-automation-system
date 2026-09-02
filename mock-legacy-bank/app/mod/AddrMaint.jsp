<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
var _acct = "";

function doLoad() {
  var v = document.getElementById("ext-gen80_input").value;
  _acct = v;
  var host = document.getElementById("ext-comp-2028");
  host.innerHTML = '<div id="loading-spinner">Legacy host communication in progress...</div>';
  xhrGet("/portal/svc/AddrLoad.do?acctId=" + encodeURIComponent(v) +
         "&_ts=" + new Date().getTime(),
    function (html) { host.innerHTML = html; });
}

function val(id) { var e = document.getElementById(id); return e ? e.value : ""; }

function doReview(acct) {
  _acct = acct;
  var slot = document.getElementById("ext-comp-2030");
  slot.innerHTML =
    '<div class="x-panel-header" id="ext-gen88a" style="margin-top:10px">Review Pending Change</div>' +
    '<table cellpadding="0" cellspacing="0" border="0" class="x-grid3" id="ext-gen88g">' +
    '<tr class="x-grid3-hd-row"><td class="x-grid3-hd-inner">FIELD</td>' +
    '<td class="x-grid3-hd-inner">NEW VALUE</td></tr>' +
    '<tr class="x-grid3-row"><td class="x-grid-cell-inner">ADDRESS</td>' +
    '<td class="x-grid-cell-inner">' + val("ext-gen86_l1") + ' ' + val("ext-gen86_l2") + '</td></tr>' +
    '<tr class="x-grid3-row"><td class="x-grid-cell-inner">CITY / ST / ZIP</td>' +
    '<td class="x-grid-cell-inner">' + val("ext-gen86_ct") + ', ' + val("ext-gen86_st") +
    ' ' + val("ext-gen86_zp") + '</td></tr></table>' +
    '<div style="margin-top:8px">' +
    '<table cellpadding="0" cellspacing="0" border="0" class="x-btn"><tr>' +
    '<td class="x-btn-ml"><i>&nbsp;</i></td>' +
    '<td class="x-btn-mc"><span class="x-btn-text" id="ext-gen89c" ' +
    'onclick="doCommit()">Commit Changes</span></td>' +
    '<td class="x-btn-mr"><i>&nbsp;</i></td></tr></table></div>' +
    '<div id="ext-comp-2032"></div>';
}

function doCommit() {
  var slot = document.getElementById("ext-comp-2032");
  slot.innerHTML = '<div id="loading-spinner">Posting maintenance to host...</div>';
  var q = "acctId=" + encodeURIComponent(_acct) +
          "&l1=" + encodeURIComponent(val("ext-gen86_l1")) +
          "&l2=" + encodeURIComponent(val("ext-gen86_l2")) +
          "&ct=" + encodeURIComponent(val("ext-gen86_ct")) +
          "&st=" + encodeURIComponent(val("ext-gen86_st")) +
          "&zp=" + encodeURIComponent(val("ext-gen86_zp"));
  xhrPost("/portal/svc/AddrCommit.do", q, function (r) {
    if (r.result === "OK") {
      slot.innerHTML = '<div class="x-msg-ok" id="ext-gen90a">' + r.msg +
        ' Confirmation ' + r.conf + '</div>';
    } else if (r.result === "BUSINESS_REJECT") {
      slot.innerHTML = '<div class="x-msg-err" id="ext-gen91a">' + r.msg + '</div>';
    } else if (r.result === "SECURITY_INTERCEPT") {
      slot.innerHTML = "";
      window.top.__sec_intercept(_acct, r.msg, "addr");
    } else {
      slot.innerHTML = '<div class="x-msg-err">Unhandled host response: ' +
        (r.msg || r.result) + '</div>';
    }
  });
}

function __onOverrideCleared(r) {
  var slot = document.getElementById("ext-comp-2032");
  if (slot) {
    slot.innerHTML = '<div class="x-msg-ok" id="ext-gen90a">' + r.msg +
      ' Confirmation ' + r.conf + '</div>';
  }
}
function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doLoad(); } }
</script>
</head>
<body style="margin:0;background:#fff">
<div class="x-panel-header" id="ext-gen50a">Address Maintenance</div>
<div class="x-panel-body">
  <table cellpadding="2" cellspacing="0" border="0"><tr>
    <td>Account Identifier:</td>
    <td><input type="text" id="ext-gen80_input" name="acctNbr" size="18" maxlength="12"
               class="x-form-field" onkeypress="kp(event)"></td>
    <td style="padding-left:8px">
      <div class="x-btn x-btn-noicon" id="ext-gen82b" onclick="doLoad()">
        <table cellpadding="0" cellspacing="0" border="0"><tr>
          <td class="x-btn-ml"><i>&nbsp;</i></td>
          <td class="x-btn-mc"><span class="x-btn-text">Load Address</span></td>
          <td class="x-btn-mr"><i>&nbsp;</i></td>
        </tr></table>
      </div>
    </td>
  </tr></table>
  <div id="ext-comp-2028" style="margin-top:10px"></div>
</div>
</body>
</html>
