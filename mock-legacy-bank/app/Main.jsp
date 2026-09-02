<html>
<head>
<title>FirstCore Banking Platform v4.2.1</title>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
function ackDisclaimer() {
  var o = document.getElementById("ext-comp-1041");
  var m = document.getElementById("dscMask");
  if (o) { o.parentNode.removeChild(o); }
  if (m) { m.parentNode.removeChild(m); }
  document.getElementById("navFrm").src = "/portal/Nav.jsp";
}
</script>
</head>
<body style="overflow:hidden">

<table width="100%" height="100%" cellpadding="0" cellspacing="0" border="0">
  <tr>
    <td colspan="2" height="52">
      <iframe name="hdrFrm" id="hdrFrm" src="/portal/Hdr.jsp"
              frameborder="0" scrolling="no" width="100%" height="52"></iframe>
    </td>
  </tr>
  <tr>
    <td width="215" valign="top" bgcolor="#dfe8f6">
      <iframe name="navFrm" id="navFrm" src="about:blank"
              frameborder="0" scrolling="auto" width="215" height="620"></iframe>
    </td>
    <td valign="top">
      <iframe name="wrkFrm" id="wrkFrm" src="/portal/Wrk.jsp"
              frameborder="0" scrolling="auto" width="100%" height="620"></iframe>
    </td>
  </tr>
</table>

<div class="x-mask" id="dscMask"></div>
<div class="x-window" id="ext-comp-1041" style="left:50%;top:140px;margin-left:-190px">
  <div class="x-window-hd" id="ext-gen30">Notice to Authorized Users</div>
  <div class="x-window-bd">
    <div style="margin-bottom:10px">
      This system is the property of the institution and is for authorized
      business use only. All activity is monitored and recorded. Unauthorized
      access is prohibited and may be subject to prosecution.
    </div>
    <table cellpadding="0" cellspacing="0" border="0" class="x-btn"><tr>
      <td class="x-btn-ml"><i>&nbsp;</i></td>
      <td class="x-btn-mc"><span class="x-btn-text" id="ext-gen33" onclick="ackDisclaimer()">I Acknowledge</span></td>
      <td class="x-btn-mr"><i>&nbsp;</i></td>
    </tr></table>
  </div>
</div>

</body>
</html>
