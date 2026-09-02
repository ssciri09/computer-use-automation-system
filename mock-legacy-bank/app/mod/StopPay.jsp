<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
function doValidate() {
  var a = document.getElementById("ext-gen70_input").value;
  var c = document.getElementById("ext-gen71_input").value;
  var host = document.getElementById("ext-comp-2025");
  host.innerHTML = '<div id="loading-spinner">Legacy host communication in progress...</div>';
  xhrPost("/portal/svc/StopPayValidate.do",
    "acctId=" + encodeURIComponent(a) + "&chkNbr=" + encodeURIComponent(c),
    function (r) {
      if (r.result === "OK") {
        host.innerHTML =
          '<div class="x-panel-header" id="ext-gen74">Confirm Stop Payment</div>' +
          '<table cellpadding="0" cellspacing="0" border="0" class="x-grid3" id="ext-gen74g">' +
          '<tr class="x-grid3-hd-row"><td class="x-grid3-hd-inner">ACCT</td>' +
          '<td class="x-grid3-hd-inner">NAME</td><td class="x-grid3-hd-inner">CHECK</td>' +
          '<td class="x-grid3-hd-inner">SVC FEE</td></tr>' +
          '<tr class="x-grid3-row" id="ext-gen74r">' +
          '<td class="x-grid-cell-inner">' + r.acct + '</td>' +
          '<td class="x-grid-cell-inner">' + r.name + '</td>' +
          '<td class="x-grid-cell-inner">' + r.chk + '</td>' +
          '<td class="x-grid-cell-inner" align="right">' + r.fee + '</td></tr></table>' +
          '<div style="margin-top:8px">' +
          '<table cellpadding="0" cellspacing="0" border="0" class="x-btn"><tr>' +
          '<td class="x-btn-ml"><i>&nbsp;</i></td>' +
          '<td class="x-btn-mc"><span class="x-btn-text" id="ext-gen75" ' +
          'onclick="doCommit(\'' + r.acct + '\',\'' + r.chk + '\')">Place Stop Payment</span></td>' +
          '<td class="x-btn-mr"><i>&nbsp;</i></td></tr></table></div>' +
          '<div id="ext-comp-2026"></div>';
      } else if (r.result === "TRANSIENT") {
        host.innerHTML = '<div class="x-msg-warn" id="ext-comp-1099">' + r.msg + '</div>';
      } else if (r.result === "BUSINESS_REJECT") {
        host.innerHTML = '<div class="x-msg-err" id="ext-gen78">' + r.msg + '</div>';
      } else {
        host.innerHTML = '<div class="x-msg-warn" id="ext-comp-1098">' + r.msg + '</div>';
      }
    });
}

function doCommit(acct, chk) {
  var slot = document.getElementById("ext-comp-2026");
  slot.innerHTML = '<div id="loading-spinner">Posting stop payment to host...</div>';
  xhrPost("/portal/svc/StopPayCommit.do",
    "acctId=" + encodeURIComponent(acct) + "&chkNbr=" + encodeURIComponent(chk),
    function (r) {
      if (r.result === "OK") {
        slot.innerHTML = '<div class="x-msg-ok" id="ext-gen79">' + r.msg +
          ' Confirmation ' + r.conf + '</div>';
      } else if (r.result === "BUSINESS_REJECT") {
        slot.innerHTML = '<div class="x-msg-err" id="ext-gen78">' + r.msg + '</div>';
      } else if (r.result === "SECURITY_INTERCEPT") {
        slot.innerHTML = "";
        window.top.__sec_intercept(acct, r.msg, "stop");
      } else {
        slot.innerHTML = '<div class="x-msg-err">Unhandled host response: ' +
          (r.msg || r.result) + '</div>';
      }
    });
}

function __onOverrideCleared(r) {
  var slot = document.getElementById("ext-comp-2026");
  if (slot) {
    slot.innerHTML = '<div class="x-msg-ok" id="ext-gen79">' + r.msg +
      ' Confirmation ' + r.conf + '</div>';
  }
}
function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doValidate(); } }
</script>
</head>
<body style="margin:0;background:#fff">
<div class="x-panel-header" id="ext-gen50s">Stop Payments</div>
<div class="x-panel-body">
  <table cellpadding="2" cellspacing="0" border="0">
    <tr><td align="right">Account Identifier:</td>
        <td><input type="text" id="ext-gen70_input" name="acctNbr" size="18" maxlength="12"
                   class="x-form-field" onkeypress="kp(event)"></td></tr>
    <tr><td align="right">Check Number:</td>
        <td><input type="text" id="ext-gen71_input" name="chkNbr" size="12" maxlength="6"
                   class="x-form-field" onkeypress="kp(event)"></td></tr>
    <tr><td>&nbsp;</td><td>
      <div class="x-btn x-btn-noicon" id="ext-gen73" onclick="doValidate()">
        <table cellpadding="0" cellspacing="0" border="0"><tr>
          <td class="x-btn-ml"><i>&nbsp;</i></td>
          <td class="x-btn-mc"><span class="x-btn-text">Validate Item</span></td>
          <td class="x-btn-mr"><i>&nbsp;</i></td>
        </tr></table>
      </div>
    </td></tr>
  </table>
  <div id="ext-comp-2025" style="margin-top:10px"></div>
</div>
</body>
</html>
