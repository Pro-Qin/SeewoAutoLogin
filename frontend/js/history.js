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

  // 同一个账号、同一种来源归一组，组内按天分节：记录一多，平铺列表看不出规律，
  // 分组之后「这个账号最近是不是总被风控」一眼就能看出来。
  var groups = [], byKey = {};
  items.forEach(function (it) {
    var key = (it.accountId || '') + '|' + (it.source || '');
    if (!byKey[key]) {
      byKey[key] = { name: it.name || it.accountId || '', source: it.source || '', items: [] };
      groups.push(byKey[key]);
    }
    byKey[key].items.push(it);
  });

  list.innerHTML = groups.map(function (g) {
    var recent = g.items.length ? String(g.items[0].time || '') : '';
    var lastDay = '', body = '';
    g.items.forEach(function (it) {
      var full = String(it.time || '');
      var day = full.slice(0, 10);
      if (day !== lastDay) {
        lastDay = day;
        body += '<div class="lh-day">' + esc(day) + '</div>';
      }
      var shortTime = full.length >= 16 ? full.slice(11, 16) : full;
      body += '<div class="lh-row' + (it.success ? '' : ' bad') + '">'
        + '<span class="lh-time" data-full="' + escAttr(full) + '" title="' + escAttr(full) + '">' + esc(shortTime) + '</span>'
        + '<span class="lh-name">' + esc(it.name || '') + '</span>'
        + '<span class="lh-src">' + esc(it.source || '') + '</span>'
        + '<span class="lh-detail">' + esc(it.detail || '') + '</span>'
        + '</div>';
    });
    return '<details class="lh-group">'
      + '<summary>'
      +   '<span class="lh-group-name">' + esc(g.name) + '</span>'
      +   '<span class="lh-group-src">' + esc(g.source) + '</span>'
      +   '<span class="lh-group-count">' + g.items.length + ' 条</span>'
      +   '<span class="lh-group-time">' + esc(recent.length >= 16 ? recent.slice(5, 16) : recent) + '</span>'
      + '</summary>'
      + '<div class="lh-group-body">' + body + '</div>'
      + '</details>';
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
  // 点日期在「时:分」与「完整年月日时分秒」之间切换
document.addEventListener('click', function (e) {
  var t = (e.target && e.target.closest) ? e.target.closest('.lh-time') : null;
  if (!t) return;
  var full = t.getAttribute('data-full') || '';
  if (!full) return;
  var expanded = t.getAttribute('data-expanded') === '1';
  t.textContent = expanded ? (full.length >= 16 ? full.slice(11, 16) : full) : full;
  t.setAttribute('data-expanded', expanded ? '0' : '1');
});

// 记录页的加载挂在 navigate 里：切页即刷新，不用在这里再绑一次点击
}
