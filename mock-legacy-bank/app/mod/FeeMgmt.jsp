<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">

function doInq() {
  var v = document.getElementById("ext-gen42_input").value;
  var host = document.getElementById("ext-comp-1097");
  host.innerHTML =
    '<div id="loading-spinner">Legacy host communication in progress...</div>';
  xhrGet("/portal/svc/AcctInq.do?acctId=" + encodeURIComponent(v) +
         "&_ts=" + new Date().getTime(),
    function (html) { host.innerHTML = html; });
}

function doWaive(acctId, feeId) {
  var slot = document.getElementById("ext-comp-1102");
  slot.innerHTML = '<div id="loading-spinner">Posting reversal to host...</div>';
  xhrPost("/portal/svc/WaiveFee.do",
    "acctId=" + encodeURIComponent(acctId) + "&feeId=" + encodeURIComponent(feeId),
    function (r) {
      if (r.result === "OK") {
        slot.innerHTML = '<div class="x-msg-ok" id="ext-gen110">' +
          r.msg + ' Confirmation ' + r.conf + '</div>';
      } else if (r.result === "BUSINESS_REJECT") {
        slot.innerHTML = '<div class="x-msg-err" id="ext-gen111">' + r.msg + '</div>';
      } else if (r.result === "SECURITY_INTERCEPT") {
        slot.innerHTML = "";
        window.top.__sec_intercept(acctId, r.msg);
      } else {
        slot.innerHTML = '<div class="x-msg-err" id="ext-gen112">' +
          'Unhandled host response: ' + (r.msg || r.result) + '</div>';
      }
    });
}

/* Called by the top frame once a human clears the override. */
function __onOverrideCleared(r) {
  var slot = document.getElementById("ext-comp-1102");
  if (slot) {
    slot.innerHTML = '<div class="x-msg-ok" id="ext-gen110">' +
      r.msg + ' Confirmation ' + r.conf + '</div>';
  }
}

function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doInq(); } }
</script>
</head>
<body style="margin:0;background:#fff">

<div class="x-panel-header" id="ext-gen40">Fee Management &mdash; Account Inquiry</div>

<div class="x-panel-body">
  <table cellpadding="2" cellspacing="0" border="0"><tr>
    <td>Account Identifier:</td>
    <td>
      <input type="text" id="ext-gen42_input" name="acctNbr" size="18"
             maxlength="12" class="x-form-field" onkeypress="kp(event)">
    </td>
    <td style="padding-left:8px">
      <div class="x-btn x-btn-noicon" id="ext-gen44" onclick="doInq()">
        <table cellpadding="0" cellspacing="0" border="0"><tr>
          <td class="x-btn-ml"><i>&nbsp;</i></td>
          <td class="x-btn-mc"><span class="x-btn-text">Execute Inquiry</span></td>
          <td class="x-btn-mr"><i>&nbsp;</i></td>
        </tr></table>
      </div>
    </td>
  </tr></table>

  <div id="ext-comp-1097" style="margin-top:10px"></div>
</div>

</body>
</html>
