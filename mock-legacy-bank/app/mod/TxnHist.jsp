<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
function goPage(n) { doHist(n); }
function doHist(page) {
  var v = document.getElementById("ext-gen60_input").value;
  var d = document.getElementById("ext-gen61_sel").value;
  var host = document.getElementById("ext-comp-2018");
  host.innerHTML = '<div id="loading-spinner">Legacy host communication in progress...</div>';
  xhrGet("/portal/svc/TxnHist.do?acctId=" + encodeURIComponent(v) +
         "&days=" + encodeURIComponent(d) + "&page=" + (page || 1) +
         "&_ts=" + new Date().getTime(),
    function (html) { host.innerHTML = html; });
}
function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doHist(1); } }
</script>
</head>
<body style="margin:0;background:#fff">
<div class="x-panel-header" id="ext-gen50t">Transaction History</div>
<div class="x-panel-body">
  <table cellpadding="2" cellspacing="0" border="0"><tr>
    <td>Account Identifier:</td>
    <td><input type="text" id="ext-gen60_input" name="acctNbr" size="18" maxlength="12"
               class="x-form-field" onkeypress="kp(event)"></td>
    <td style="padding-left:10px">Period:</td>
    <td><select id="ext-gen61_sel" name="perDays" class="x-form-field">
          <option value="30">Last 30 days</option>
          <option value="60">Last 60 days</option>
          <option value="90">Last 90 days</option>
        </select></td>
    <td style="padding-left:8px">
      <div class="x-btn x-btn-noicon" id="ext-gen62b" onclick="doHist(1)">
        <table cellpadding="0" cellspacing="0" border="0"><tr>
          <td class="x-btn-ml"><i>&nbsp;</i></td>
          <td class="x-btn-mc"><span class="x-btn-text">Retrieve History</span></td>
          <td class="x-btn-mr"><i>&nbsp;</i></td>
        </tr></table>
      </div>
    </td>
  </tr></table>
  <div id="ext-comp-2018" style="margin-top:10px"></div>
</div>
</body>
</html>
