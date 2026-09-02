<html>
<head>
<title>FirstCore Banking Platform - Sign On</title>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript" src="/ext-core.js"></script>
<script type="text/javascript">
function doSubmit() { document.forms['logonForm'].submit(); }
function kp(e) { var c = e ? e.keyCode : window.event.keyCode; if (c == 13) { doSubmit(); } }
</script>
</head>
<body>
<table width="100%" height="100%" cellpadding="0" cellspacing="0" border="0"><tr>
<td align="center" valign="middle">
<table cellpadding="0" cellspacing="0" border="0" width="420"><tr><td>
  <div class="x-panel-header" id="ext-gen11">FirstCore Banking Platform &nbsp;&mdash;&nbsp; Operator Sign On</div>
  <div class="x-panel-body">
    <form name="logonForm" method="POST" action="/portal/j_security_check">
    <table cellpadding="3" cellspacing="0" border="0" width="100%">
      <tr>
        <td width="110" align="right">User ID:</td>
        <td><input type="text" name="j_username" id="ext-gen17_u" size="24"
                   class="x-form-field" onkeypress="kp(event)"></td>
      </tr>
      <tr>
        <td align="right">Password:</td>
        <td><input type="password" name="j_password" id="ext-gen19_p" size="24"
                   class="x-form-field" onkeypress="kp(event)"></td>
      </tr>
      <tr>
        <td>&nbsp;</td>
        <td>
          <table cellpadding="0" cellspacing="0" border="0" class="x-btn"><tr>
            <td class="x-btn-ml"><i>&nbsp;</i></td>
            <td class="x-btn-mc"><span class="x-btn-text" id="ext-gen21" onclick="doSubmit()">Sign On</span></td>
            <td class="x-btn-mr"><i>&nbsp;</i></td>
          </tr></table>
        </td>
      </tr>
    </table>
    </form>
    <!--ERRSLOT-->
    <div style="margin-top:10px;border-top:1px solid #ccc;padding-top:6px;color:#666">
      Test credentials: <b>operator</b> / <b>letmein</b>
    </div>
  </div>
</td></tr></table>
</td></tr></table>
</body>
</html>
