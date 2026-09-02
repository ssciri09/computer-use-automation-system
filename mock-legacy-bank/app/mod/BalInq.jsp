<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
function doRetrieve() {
  var v = document.getElementById("ext-gen52_input").value;
  var host = document.getElementById("ext-comp-2010");
  host.innerHTML = '<div id="loading-spinner">Legacy host communication in progress...</div>';
  xhrGet("/portal/svc/BalInq.do?acctId=" + encodeURIComponent(v) +
         "&_ts=" + new Date().getTime(),
    function (html) { host.innerHTML = html; });
}
function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doRetrieve(); } }
</script>
</head>
<body style="margin:0;background:#fff">
<div class="x-panel-header" id="ext-gen50b">Balance Inquiry</div>
<div class="x-panel-body">
  <table cellpadding="2" cellspacing="0" border="0"><tr>
    <td>Account Identifier:</td>
    <td><input type="text" id="ext-gen52_input" name="acctNbr" size="18" maxlength="12"
               class="x-form-field" onkeypress="kp(event)"></td>
    <td style="padding-left:8px">
      <div class="x-btn x-btn-noicon" id="ext-gen54" onclick="doRetrieve()">
        <table cellpadding="0" cellspacing="0" border="0"><tr>
          <td class="x-btn-ml"><i>&nbsp;</i></td>
          <td class="x-btn-mc"><span class="x-btn-text">Retrieve Balances</span></td>
          <td class="x-btn-mr"><i>&nbsp;</i></td>
        </tr></table>
      </div>
    </td>
  </tr></table>
  <div id="ext-comp-2010" style="margin-top:10px"></div>
</div>
</body>
</html>
