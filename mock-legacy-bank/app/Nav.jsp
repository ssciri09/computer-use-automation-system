<html>
<head>
<link rel="stylesheet" type="text/css" href="/legacy.css">
<script type="text/javascript">
function tgl(id, ec) {
  var n = document.getElementById(id);
  var g = document.getElementById(ec);
  if (n.style.display == "none" || n.style.display == "") {
    n.style.display = "block"; g.innerHTML = "-";
  } else {
    n.style.display = "none"; g.innerHTML = "+";
  }
}
function sel(el) {
  var all = document.getElementsByTagName("div");
  for (var i = 0; i < all.length; i++) {
    if (all[i].className.indexOf("x-tree-node-el") === 0) {
      all[i].className = "x-tree-node-el";
    }
  }
  el.className = "x-tree-node-el x-tree-selected";
}
function openMod(el, mod) {
  sel(el);
  parent.frames["wrkFrm"].loadModule(mod);
}
</script>
</head>
<body style="margin:0;background:#dfe8f6">
<div style="padding:5px 6px;font-weight:bold;color:#15428b;border-bottom:1px solid #99bbe8">
  Application Menu
</div>
<div style="padding:4px 2px">

  <div class="x-tree-node-el" id="ext-gen80" onclick="sel(this)">
    <span class="x-tree-ec" id="ext-gen81" onclick="tgl('nd-inq','ext-gen81');event.cancelBubble=true">+</span>
    <span class="x-tree-node-text">Inquiry</span>
  </div>
  <div id="nd-inq" style="display:none;margin-left:16px">
    <div class="x-tree-node-el" id="ext-gen82" onclick="openMod(this,'BalInq')">
      <span class="x-tree-ec">&middot;</span><span class="x-tree-node-text">Balance Inquiry</span>
    </div>
    <div class="x-tree-node-el" id="ext-gen83" onclick="openMod(this,'TxnHist')">
      <span class="x-tree-ec">&middot;</span><span class="x-tree-node-text">Transaction History</span>
    </div>
  </div>

  <div class="x-tree-node-el" id="ext-gen88" onclick="sel(this)">
    <span class="x-tree-ec" id="ext-gen89" onclick="tgl('nd-svc','ext-gen89');event.cancelBubble=true">+</span>
    <span class="x-tree-node-text">Account Servicing</span>
  </div>
  <div id="nd-svc" style="display:none;margin-left:16px">
    <div class="x-tree-node-el" id="ext-gen90" onclick="openMod(this,'StopPay')">
      <span class="x-tree-ec">&middot;</span><span class="x-tree-node-text">Stop Payments</span>
    </div>
    <div class="x-tree-node-el" id="ext-gen91" onclick="openMod(this,'FeeMgmt')">
      <span class="x-tree-ec">&middot;</span><span class="x-tree-node-text">Fee Management</span>
    </div>
    <div class="x-tree-node-el" id="ext-gen92" onclick="openMod(this,'AddrMaint')">
      <span class="x-tree-ec">&middot;</span><span class="x-tree-node-text">Address Maintenance</span>
    </div>
  </div>

  <div class="x-tree-node-el" id="ext-gen93" onclick="sel(this)">
    <span class="x-tree-ec" id="ext-gen94" onclick="tgl('nd-rpt','ext-gen94');event.cancelBubble=true">+</span>
    <span class="x-tree-node-text">Reports</span>
  </div>
  <div id="nd-rpt" style="display:none;margin-left:16px">
    <div class="x-tree-node-el" id="ext-gen96" onclick="openMod(this,'FeeReg')">
      <span class="x-tree-ec">&middot;</span><span class="x-tree-node-text">Daily Fee Register</span>
    </div>
  </div>

</div>
</body>
</html>
