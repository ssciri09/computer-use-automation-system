<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
function doGen() {
  var d = document.getElementById("ext-gen92_input").value;
  var host = document.getElementById("ext-comp-2038");
  host.innerHTML = '<div id="loading-spinner">Legacy host communication in progress...</div>';
  xhrGet("/portal/svc/FeeReg.do?bizDate=" + encodeURIComponent(d) +
         "&_ts=" + new Date().getTime(),
    function (html) { host.innerHTML = html; });
}
function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doGen(); } }
</script>
</head>
<body style="margin:0;background:#fff">
<div class="x-panel-header" id="ext-gen50r">Daily Fee Register</div>
<div class="x-panel-body">
  <table cellpadding="2" cellspacing="0" border="0"><tr>
    <td>Business Date (MM/DD/YYYY):</td>
    <td><input type="text" id="ext-gen92_input" name="bizDate" size="14" maxlength="10"
               class="x-form-field" onkeypress="kp(event)"></td>
    <td style="padding-left:8px">
      <div class="x-btn x-btn-noicon" id="ext-gen94b" onclick="doGen()">
        <table cellpadding="0" cellspacing="0" border="0"><tr>
          <td class="x-btn-ml"><i>&nbsp;</i></td>
          <td class="x-btn-mc"><span class="x-btn-text">Generate Report</span></td>
          <td class="x-btn-mr"><i>&nbsp;</i></td>
        </tr></table>
      </div>
    </td>
  </tr></table>
  <div style="margin-top:4px;color:#666">Register is retained for the current and prior business date.</div>
  <div id="ext-comp-2038" style="margin-top:10px"></div>
</div>
</body>
</html>
