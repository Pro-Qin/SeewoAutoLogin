// qr.js — 密码登录、扫码登录状态渲染、添加账号页展开与扫码框阶段。依赖：core.js、ui.js、nav.js。

// ===== Login =====
function handleLogin() {
  const u = document.getElementById('usernameInput').value.trim();
  const p = document.getElementById('passwordInput').value;
  const d = document.getElementById('displayNameInput').value.trim();
  if (!u || !p) { document.getElementById('loginStatus').textContent = '请输入账号和密码'; return; }
  send({type:'login', username:u, password:p, displayName:d});
}

// ===== QR Login =====
function renderQrState(msg) {
  ['startQrBtn','refreshQrBtn','cancelQrBtn'].forEach(id => { const el = document.getElementById(id); if(el) el.style.display='none'; });
  const active = ['creating','waiting-scan','waiting-confirm','completing'].includes(msg.state);
  const s = (id) => document.getElementById(id);
  if (s('startQrBtn')) s('startQrBtn').style.display = active ? 'none' : '';
  if (s('cancelQrBtn')) s('cancelQrBtn').style.display = active ? '' : 'none';
  if (s('refreshQrBtn')) s('refreshQrBtn').style.display = ['expired','network-error','denied'].includes(msg.state)?'':'none';
  if (msg.imageData && s('qrImage')) { s('qrImage').src = msg.imageData; s('qrContainer').style.display = 'block'; }
  if (s('qrStatus')) s('qrStatus').textContent = msg.text||'';
  if (msg.state === 'succeeded' && s('qrContainer')) s('qrContainer').style.display = 'none';
  if (!active && msg.state !== 'succeeded') { if(s('qrContainer')) s('qrContainer').style.display = 'none'; if(s('qrCountdown')) s('qrCountdown').textContent = ''; }
  // 扫码状态联动界面：成功亮起对勾，取消/过期/失败退回可重新开始的状态
  if (typeof setQrStage === "function") {
    var st = (msg && msg.state) || "";
    if (st === "succeeded") setQrStage("success");
    else if (st === "cancelled" || st === "expired" || st === "failed") setQrStage("idle");
  }
}

// ===== 扫码前的确认弹窗 =====
// 扫码凭据由希沃签发且无法在失效后恢复；这里先把利弊讲清楚，再让用户选。
// ===== 添加账号页：双圆钮展开 / 收缩 =====
// 点一个展开、另一个变灰；再点已展开的收回；点另一个则先前自动收缩。
function navigateToAdd() {
  // 直接进添加账号页，不自动展开任何一侧（由用户自己点圆钮）
  navigate("add");
}

function pickSide(which) {
  var aw = document.getElementById('addwrap');
  var sP = document.getElementById('sidePw');
  var sQ = document.getElementById('sideQr');
  if (!aw || !sP || !sQ) return;
  var openPw = (which === 'pw');
  var alreadyOpen = (openPw && sP.classList.contains('open')) || (!openPw && sQ.classList.contains('open'));
  function apply(side, isOpen, isDim) {
    side.classList.toggle('open', isOpen);
    side.classList.toggle('dim', isDim);
  }
  if (alreadyOpen) {
    aw.classList.remove('exp');
    apply(sP, false, false);
    apply(sQ, false, false);
    return;
  }
  aw.classList.add('exp');
  apply(sP, openPw, !openPw);
  apply(sQ, !openPw, openPw);
}

// 扫码区四个阶段：idle(模糊+开始按钮) / loading(转圈) / ready(揭开模糊) / success(对勾)
function setQrStage(stage) {
  var f = document.getElementById('qrframe');
  var mk = document.getElementById('qrmask');
  var sp = document.getElementById('qrspin');
  var sg = document.getElementById('qrStatus');
  var ph = document.getElementById('qrPlaceholder');
  var img = document.getElementById('qrImage');
  if (!f) return;
  if (stage === 'loading') {
    if (mk) mk.style.display = 'none';
    if (sp) sp.style.display = 'grid';
    if (sg) sg.innerHTML = '<b>正在获取二维码…</b>';
    return;
  }
  if (stage === 'ready') {
    if (sp) sp.style.display = 'none';
    if (mk) mk.style.display = 'none';
    f.classList.remove('blur');
    f.classList.add('ready');
    if (ph) ph.style.display = 'none';
    if (img) img.style.display = 'block';
    if (sg) sg.textContent = '请用手机扫描二维码';
    return;
  }
  if (stage === 'success') {
    f.classList.add('done');
    if (sg) sg.innerHTML = '<b>登录成功</b>';
    return;
  }
  f.classList.add('blur');
  f.classList.remove('ready');
  f.classList.remove('done');
  if (mk) mk.style.display = 'grid';
  if (sp) sp.style.display = 'none';
  if (ph) ph.style.display = 'block';
  if (img) img.style.display = 'none';
  if (sg) sg.textContent = '';
}

// 扫码被后台取消 / 超时 / 失败时，把界面退回初始态并重新露出「开始扫码」按钮。
// 注意：这里用轮询而不是 MutationObserver —— 回调里会改 qrStatus 的文字，
// 用 observer 监视它自己会造成无限递归，界面会直接卡死。
// 判据用 qrframe 是否还停在 ready/done 状态，重置后条件不再成立，天然不会重复执行。
function watchQrStatus() {
  if (window.__qrWatchStarted) return;
  window.__qrWatchStarted = true;
  setInterval(function () {
    var f = document.getElementById('qrframe');
    var sg = document.getElementById('qrStatus');
    if (!f || !sg) return;
    var busy = f.classList.contains('ready') || f.classList.contains('done');
    if (!busy) return;
    var t = sg.textContent || '';
    if (!/取消|失败|过期|无效|错误/.test(t)) return;
    setQrStage('idle');
    sg.textContent = t;
  }, 600);
}

function bindAddPage() {
  bindClick('cirPw', function () { pickSide('pw'); });
  bindClick('cirQr', function () { pickSide('qr'); });
  var sP = document.getElementById('sidePw');
  var sQ = document.getElementById('sideQr');
  if (sP) { var l1 = sP.querySelector('.lbl'); if (l1) l1.addEventListener('click', function () { pickSide('pw'); }); }
  if (sQ) { var l2 = sQ.querySelector('.lbl'); if (l2) l2.addEventListener('click', function () { pickSide('qr'); }); }
  var fold = document.getElementById('foldBatch');
  if (fold) {
    var fb = fold.querySelector('button');
    if (fb) fb.addEventListener('click', function () { fold.classList.toggle('open'); });
  }
  // C# 把二维码图片写进 qrImage.src 后，onload 触发，此时才揭开模糊
  var img = document.getElementById('qrImage');
  if (img) img.addEventListener('load', function () { if (this.getAttribute('src')) setQrStage('ready'); });
  bindClick('startQrBtn', function () {
    setQrStage('loading');
    send({ type: 'start-qr' });
  });
  // 刷新 = 重新要一张二维码 + 切到 loading。
  // 原先一半写在 HTML 的 inline onclick（发消息）、一半写在这里（切状态），
  // 改行为时两处都得改，漏一处就出现「切了状态但没发请求」这类半截操作。
  bindClick('refreshQrBtn', function () {
    setQrStage('loading');
    send({ type: 'start-qr' });
  });
  // 「扫码过期？试试密码登录」里的链接：等价于点左侧那个密码登录大圆钮
  bindClick('qrTipSwitch', function () { pickSide('pw'); });
  watchQrStatus();
}
// 说明：扫码前的确认弹窗（qrWarnOverlay）早已并入扫码面板下方的常驻说明。
// 相关绑定与 confirmQrLogin 一起删掉了：留着一个调用已不存在函数的入口，
// 只会让后来的人以为还有一个界面在等着被接上。
