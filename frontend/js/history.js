// history.js — 登录记录页。依赖：core.js、ui.js、accounts.js。

// ===== 登录记录 =====
function loadLoginHistory() {
  var sel = document.getElementById('loginHistoryAccount');
  var src = document.getElementById('loginHistorySource');
  send({
    type: 'get-login-history',
    accountId: sel ? (sel.value || '') : '',
    source: src ? (src.value || '') : '',
    limit: 200
  });
}
function renderLoginHistory(msg) {
  var list = document.getElementById('loginHistoryList');
  if (!list) return;
  msg = msg || {};

  var sel = document.getElementById('loginHistoryAccount');
  if (sel && Array.isArray(msg.accounts)) {
    var current = sel.value || '';
    var opts = ['<option value="">全部账号</option>'];
    msg.accounts.forEach(function (a) {
      opts.push('<option value="' + escAttr(a.id) + '">' + esc(a.name || a.id) + '</option>');
    });
    sel.innerHTML = opts.join('');
    sel.value = current;
  }

  if (msg.error) { list.innerHTML = '<div class="backup-empty">读取失败：' + esc(msg.error) + '</div>'; return; }
  var items = msg.items || [];
  if (!items.length) { list.innerHTML = '<div class="backup-empty">还没有登录记录</div>'; return; }

  list.innerHTML = items.map(function (it) {
    return '<div class="lh-row' + (it.success ? '' : ' bad') + '">'
      + '<span class="lh-time">' + esc(it.time) + '</span>'
      + '<span class="lh-name">' + esc(it.name || '') + '</span>'
      + '<span class="lh-src">' + esc(it.source || '') + '</span>'
      + '<span class="lh-detail">' + esc(it.detail || '') + '</span>'
      + '</div>';
  }).join('');
}
function bindLoginHistory() {
  // 「续期」按钮本身带 inline onclick，这里再绑一次会让一次点击发两条 renew-account：
  // 第二条必然撞上后台「正在巡检」的互斥，弹出「请稍候再试」，看起来就像续期被触发了两次。
  bindClick('actRenewAll', actRenewAll);
  bindClick('loginHistoryRefresh', loadLoginHistory);
  bindClick('loginHistoryClear', function () {
    send({ type: 'clear-login-history' });
    showToast('登录记录已清空', 'ok');
  });
  var sel = document.getElementById('loginHistoryAccount');
  if (sel) sel.addEventListener('change', loadLoginHistory);
  var src = document.getElementById('loginHistorySource');
  if (src) src.addEventListener('change', loadLoginHistory);
  // 记录页的加载挂在 navigate 里：切页即刷新，不用在这里再绑一次点击
}
