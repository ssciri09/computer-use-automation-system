<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript">
function loadModule(mod) {
  var host = document.getElementById("ext-comp-1055");
  document.getElementById("ext-gen51").innerHTML = mod;
  host.innerHTML = '<div id="loading-spinner">Legacy host communication in progress...</div>';
  window.setTimeout(function () {
    host.innerHTML =
      '<iframe name="modFrm" id="modFrm" src="/portal/mod/' + mod + '.jsp" ' +
      'frameborder="0" scrolling="auto" width="100%" height="560"></iframe>';
  }, 1200);
}
</script>
</head>
<body style="margin:0;background:#d0dced">
<div class="x-toolbar" id="ext-gen50">
  Workspace &nbsp;&raquo;&nbsp; <span id="ext-gen51">no module loaded</span>
</div>
<div id="ext-comp-1055" style="padding:6px">
  <div class="x-panel-body" style="color:#666">
    Select a function from the application menu.
  </div>
</div>
</body>
</html>
