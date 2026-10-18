// health.js — 自检状态条（网关/hosts/权限/希沃）、账号健康巡检、健康条折叠。依赖：core.js、ui.js、accounts.js。

// ===== 自检状态栏（SSO 网关 / hosts 映射 / 管理员权限 / 希沃进程）=====
function triLevel(v, falseLevel) {
  if (v === undefined || v === null) return 'unknown';
  return v ? 'ok' : (falseLevel || 'error');
}
function setCheckItem(key, level, label, sub) {
  const el = document.getElementById('sc-' + key);
  if (!el) return;
  el.classList.remove('ok', 'warn', 'error', 'unknown');
  el.classList.add(level || 'unknown');
  const l = el.querySelector('.sc-label');
  if (l && label) l.textContent = label;
  const s = el.querySelector('.sc-sub');
  if (s) { s.textContent = sub || ''; s.style.display = sub ? '' : 'none'; }
  el.title = sub ? (label + ' ' + sub) : label;
}
function renderSelfCheck(msg) {
  const st = (msg && msg.status) ? msg.status : (msg || {});
  state.status = st;
  resetRepairButton();

  const port = (st.gatewayPort === undefined || st.gatewayPort === null || st.gatewayPort === '') ? '' : String(st.gatewayPort);
  let gwLevel = triLevel(st.gatewayRunning);
  let gwSub = '';
  if (gwLevel !== 'unknown') {
    gwSub = port ? (':' + port) : '';
    if (!st.gatewayRunning) gwSub = (gwSub ? gwSub + ' · ' : '') + '未运行';
  }
  // 网关没监听在希沃固定请求的端口上时，希沃拿不到账号列表、入口不会出现：必须显式报红
  if (st.gatewayPortWarning) {
    gwLevel = 'error';
    gwSub = (port ? ':' + port + ' · ' : '') + '端口不符（希沃需 ' + (st.expectedPort || 24300) + '）';
  }
  setCheckItem('gateway', gwLevel, 'SSO 网关', gwSub);

  const hostsLevel = triLevel(st.hostsOk);
  setCheckItem('hosts', hostsLevel, 'hosts 映射', hostsLevel === 'unknown' ? '' : (st.hostsOk ? '' : '未配置'));

  const adminLevel = triLevel(st.isAdmin);
  setCheckItem('admin', adminLevel, '管理员权限', adminLevel === 'unknown' ? '' : (st.isAdmin ? '' : '未提权'));

  const seewoLevel = triLevel(st.seewoRunning, 'warn');
  let seewoSub = '';
  if (seewoLevel === 'ok') {
    // 进程开着不代表希沃真的连上了本程序：拉过一次账号列表才算
    if (st.lastSsoMinutes === null || st.lastSsoMinutes === undefined)
      seewoSub = '运行中 · 还没收到过请求';
    else
      seewoSub = '运行中 · 最近请求 ' + (st.lastSsoMinutes < 1 ? '刚刚' : (st.lastSsoMinutes + ' 分钟前'));
  } else if (seewoLevel === 'warn') {
    seewoSub = '未运行';
  }
  setCheckItem('seewo', seewoLevel, '希沃进程', seewoSub);

  const bar = document.getElementById('selfcheckBar');
  if (bar) {
    const auto = (st.autoStartEnabled === undefined || st.autoStartEnabled === null) ? '未知' : (st.autoStartEnabled ? '已开启' : '未开启');
    bar.title = '开机自启：' + auto + (st.autoStartMode ? '（' + st.autoStartMode + '）' : '')
      + (st.gatewayPortWarning ? '\n⚠ ' + st.gatewayPortWarning : '');
  }
}
let repairTimer = null;
function resetRepairButton() {
  if (repairTimer) { clearTimeout(repairTimer); repairTimer = null; }
  const btn = document.getElementById('repairBtn');
  if (!btn) return;
  btn.disabled = false;
  btn.textContent = '一键修复';
}
function repairSso() {
  const btn = document.getElementById('repairBtn');
  send({ type: 'repair-sso' });
  if (btn) { btn.disabled = true; btn.textContent = '修复中…'; }
  if (repairTimer) clearTimeout(repairTimer);
  repairTimer = setTimeout(resetRepairButton, 3000);
}

// ===== 账号健康巡检 =====
function healthLevel(s) { return s === 'ok' ? 'ok' : (s === 'bad' ? 'bad' : (s === 'warn' ? 'warn' : 'unknown')); }
let healthTimer = null;
function runHealthCheck() {
  const btn = document.getElementById('actHealthCheck');
  const sum = document.getElementById('healthSummary');
  send({ type: 'health-check' });
  if (btn) { btn.disabled = true; btn.textContent = '巡检中…'; }
  if (sum) { sum.textContent = '巡检中…'; sum.className = 'selfcheck-summary'; }
  if (healthTimer) clearTimeout(healthTimer);
  healthTimer = setTimeout(function() {
    healthTimer = null;
    const b = document.getElementById('actHealthCheck');
    if (b) { b.disabled = false; b.textContent = '健康巡检'; }
    const s = document.getElementById('healthSummary');
    if (s && s.textContent === '巡检中…') s.textContent = '巡检超时';
  }, 30000);
}
function renderHealth(msg) {
  msg = msg || {};
  const btn = document.getElementById('actHealthCheck');
  const sum = document.getElementById('healthSummary');
  if (msg.running) {
    if (btn) { btn.disabled = true; btn.textContent = '巡检中…'; }
    if (sum) { sum.textContent = '巡检中…'; sum.className = 'selfcheck-summary'; }
    return;
  }
  if (healthTimer) { clearTimeout(healthTimer); healthTimer = null; }
  if (btn) { btn.disabled = false; btn.textContent = '健康巡检'; }
  const map = {};
  (msg.results || []).forEach(function(r) {
    if (!r || r.id === undefined || r.id === null) return;
    map[r.id] = { state: healthLevel(r.state), message: r.message || '' };
  });
  state.health = map;
  renderAccounts();
  let ok = 0, bad = 0, unk = 0;
  Object.keys(map).forEach(function(k) {
    const s = map[k].state;
    if (s === 'ok') ok++;
    else if (s === 'bad') bad++;
    else unk++;
  });
  if (sum) {
    sum.textContent = '巡检完成：' + ok + ' 正常 / ' + bad + ' 异常' + (unk ? ' / ' + unk + ' 未知' : '');
    sum.className = 'selfcheck-summary ' + (bad ? 'error' : (unk && !ok ? 'warn' : 'ok'));
  }
}

// ===== 健康条 =====
// 默认只显示一句话，把 gateway / hosts / 权限 / 希沃进程这些技术细节收进展开区。
// 有任何一项异常时自动展开一次，让问题显眼。
var healthAutoExpanded = false;
function syncHealthBar() {
  var dot = document.getElementById('healthDot');
  var text = document.getElementById('healthText');
  var bar = document.getElementById('selfcheckBar');
  if (!dot || !text) return;

  // 判据直接取自检项本身的颜色（网关 / hosts / 权限 / 希沃进程）。
  // 原先读的 healthSummary 只在账号巡检之后才有值，空着的时候这里永远显示「一切正常」，
  // 网关没起来、hosts 没写、没提权都不会反映出来 —— 那是界面唯一常显的健康入口。
  var problems = [];
  var unknown = 0;
  var items = document.querySelectorAll('#selfcheckItems [id^="sc-"]');
  for (var i = 0; i < items.length; i++) {
    var el = items[i];
    // 希沃进程没开是常态（老师没在用），不该把它算成「不健康」。
    // 但「开着却一次请求都没发过来」是另一回事——那说明快捷入口根本没生效。
    if (el.id === 'sc-seewo') {
      var seewoSubEl = el.querySelector('.sc-sub');
      if (seewoSubEl && (seewoSubEl.textContent || '').indexOf('还没收到过请求') >= 0)
        problems.push('希沃没连上（快捷入口不会出现）');
      continue;
    }
    if (el.classList.contains('unknown')) { unknown++; continue; }
    if (!el.classList.contains('error') && !el.classList.contains('warn')) continue;
    var labelEl = el.querySelector('.sc-label');
    var label = labelEl ? (labelEl.textContent || '').trim() : '';
    var subEl = el.querySelector('.sc-sub');
    var sub = subEl ? (subEl.textContent || '').trim() : '';
    problems.push(label + (sub ? '（' + sub + '）' : ''));
  }
  var bad = problems.length > 0;
  dot.className = 'health-dot' + (bad ? ' bad' : '');
  if (bad) text.textContent = '需要处理：' + problems.join('、');
  else if (unknown > 0) text.textContent = '正在检测…';   // 还没拿到结果时别说「一切正常」
  else text.textContent = '一切正常';
  if (bad && bar && !healthAutoExpanded) {
    healthAutoExpanded = true;
    bar.classList.add('open');
    var hint = document.getElementById('healthHint');
    if (hint) hint.textContent = '收起 ‹';
  }
}
function bindHealthBar() {
  var head = document.getElementById('healthBar');
  var bar = document.getElementById('selfcheckBar');
  if (head && bar) {
    head.addEventListener('click', function (e) {
      if (e.target.closest && e.target.closest('button')) return;
      var open = bar.classList.toggle('open');
      var hint = document.getElementById('healthHint');
      if (hint) hint.textContent = open ? '收起 ‹' : '展开查看细节 ›';
    });
  }
  var items = document.getElementById('selfcheckItems');
  if (items && window.MutationObserver) {
    new MutationObserver(syncHealthBar).observe(items, { attributes: true, subtree: true, attributeFilter: ['class'] });
  }
  var sum = document.getElementById('healthSummary');
  if (sum && window.MutationObserver) {
    new MutationObserver(syncHealthBar).observe(sum, { childList: true, characterData: true, subtree: true });
  }
  syncHealthBar();
}
