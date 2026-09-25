// ===== C# ↔ JS Communication =====
function send(msg) {
  try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch(e) {}
}
try {
  window.chrome.webview.addEventListener('message', function(e) {
    handleCSharpMessage(e.data);
  });
} catch (e) { /* 非 WebView2 环境（例如浏览器预览）忽略 */ }

// ===== App State =====
let state = { currentPage:'accounts', accounts:[], selectedId:null, config:{}, qrActive:false, needsPassword:false, particles:true,
  maxVisible:6, status:null, health:{}, backupsLoading:false, filterTag:'' };

// 生效区账号上限：由 init 消息 config.maxVisibleAccounts 下发，缺省 6
function maxVisible() {
  const n = parseInt(state.maxVisible, 10);
  return (isFinite(n) && n > 0) ? n : 6;
}
function applyConfig(cfg) {
  if (!cfg || typeof cfg !== 'object') return;
  state.config = cfg;
  const n = parseInt(cfg.maxVisibleAccounts, 10);
  state.maxVisible = (isFinite(n) && n > 0) ? n : maxVisible();
  updateLimitHint();
}
function updateLimitHint() {
  const hint = document.getElementById('activeLimitHint');
  if (hint) hint.textContent = '展示给希沃 · 最多' + maxVisible() + '个';
}

// ===== Navigation =====
function navigate(page) {
  closeFab();
  state.currentPage = page; state.selectedId = null;
  document.querySelectorAll('.nav-item').forEach(el => el.classList.toggle('active', el.dataset.page === page));
  document.querySelectorAll('.page').forEach(el => el.classList.toggle('active', el.id === 'page-' + page));
  const t = { accounts:'账号列表', add:'添加账号', qr:'扫码登录', settings:'设置', about:'关于' };
  document.getElementById('pageTitle').textContent = t[page] || '';
  if (page === 'accounts') renderAccounts();
  if (page === 'settings') { send({ type: 'get-credential-lifetime' }); send({ type: 'get-switch-pin' }); }
  moveNavIndicator();
}

// ===== Handle Messages from C# =====
function handleCSharpMessage(raw) {
  let msg = raw;
  if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch (e) { return; } }
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'init':
      if (msg.config) applyConfig(msg.config);
      state.accounts = msg.accounts || []; state.health = {}; renderAccounts(); updateStatusBar();
      if (msg.version) setVersion(msg.version);
      if (msg.needsPassword) showLock();
      if (msg.status) renderSelfCheck(msg.status);
      if (msg.health) renderHealth(msg.health);
      break;
    case 'config':
      applyConfig(msg.config || msg);
      break;
    case 'account-list':
      state.accounts = msg.accounts || []; state.selectedId = null; state.health = {}; renderAccounts(); updateStatusBar();
      if (msg.config) applyConfig(msg.config);
      break;
    case 'switch-pin': {
      var spEnabled = !!msg.enabled;
      var spCheck = document.getElementById('useSwitchPinCheck');
      var spPanel = document.getElementById('switchPinPanel');
      var spStatus = document.getElementById('switchPinStatus');
      if (spCheck) {
        switchPinSuppressChange = true;
        spCheck.checked = spEnabled;
        switchPinSuppressChange = false;
      }
      if (spPanel) spPanel.style.display = spEnabled ? 'block' : 'none';
      if (spStatus) spStatus.textContent = spEnabled
        ? '已启用：切换生效账号前需要输入口令'
        : '未启用：切换账号不验证'; 
      break;
    }
    case 'credential-lifetime':
      var cl = document.getElementById('credentialLifetime');
      if (cl) {
        var ctext = msg.text || '';
        if (!ctext || ctext.indexOf('样本不足') >= 0) { cl.style.display = 'none'; }
        else { cl.textContent = ctext; cl.style.display = 'block'; }
      }
      break;
    case 'terms':
      window.__termsText = msg.text || '';
      renderTerms(window.__termsText);
      break;
    case 'login-status': 
      const ls = document.getElementById('loginStatus');
      if (ls) { ls.textContent = msg.text; ls.style.display = msg.text ? '' : 'none'; }
      break;
    case 'qr-state': renderQrState(msg); break;
    case 'qr-countdown': document.getElementById('qrCountdown').textContent = msg.text; break;
    case 'settings':
      if (msg.passwordEnabled !== undefined) { document.getElementById('usePasswordCheck').checked = msg.passwordEnabled; document.getElementById('passwordPanel').style.display = msg.passwordEnabled ? 'block' : 'none'; }
      if (msg.passwordSet !== undefined) document.getElementById('passwordStatus').textContent = msg.passwordSet ? '密码已设置' : '密码未设置';
      if (msg.rotationEnabled!==undefined) document.getElementById('rotationEnabledCheck').checked = msg.rotationEnabled;
      if (msg.rotationGroupSize!==undefined) document.getElementById('rotationGroupSize').value = msg.rotationGroupSize;
      if (msg.autoStart!==undefined) document.getElementById('autoStartCheck').checked = msg.autoStart;
      if (msg.minimizeToTray!==undefined) document.getElementById('minimizeToTrayCheck').checked = msg.minimizeToTray;
      if (msg.startMinimized!==undefined) document.getElementById('startMinimizedCheck').checked = msg.startMinimized;
      if (msg.autoShowOverlay!==undefined) document.getElementById('autoShowOverlayCheck').checked = msg.autoShowOverlay;
      if (msg.autoCheckUpdate!==undefined) document.getElementById('autoCheckUpdateCheck').checked = msg.autoCheckUpdate;
      if (msg.autoInstallAfterDownload!==undefined) setAutoInstallSwitch(msg.autoInstallAfterDownload);
      break;
    case 'unlock-status': document.getElementById('unlockStatus').textContent = msg.text; break;
    case 'unlock-success': hideLock(); break;
    case 'gateway-status': document.getElementById('statusDot').style.background = msg.running ? '#34C759' : '#ff3b30'; break;
    case 'seewo-status':
      const ss = document.getElementById('seewoStatus');
      if (!ss) break;
      if (!msg.running) { ss.innerHTML = '<span style="color:#ff3b30">未运行</span>'; break; }
      var statusHtml = '';
      if (msg.loggedIn) statusHtml += '<span style="color:#34C759">● 已登录</span>';
      else statusHtml += '<span style="color:#ff9500">○ 未登录</span>';
      statusHtml += ' · 句柄:' + (msg.hwnd||'-');
      statusHtml += ' · ' + (msg.width||0)+'x'+(msg.height||0);
      statusHtml += ' · 账号 '+(msg.active||0)+'/'+(msg.accounts||0);
      statusHtml += ' · '+ (msg.lastRefresh||'');
      ss.innerHTML = statusHtml;
      break;
    case 'update-status': renderUpdateStatus(msg); break;
    case 'version': setVersion(msg.current); break;
    case 'status': renderSelfCheck(msg.status || msg); break;
    case 'health': renderHealth(msg); break;
    case 'batch-import-result': renderBatchImportResult(msg); break;
    case 'csv-imported': renderCsvImportResult(msg); break;
    case 'backups': renderBackups(msg); break;
    case 'toast': showToast(msg.text, msg.level); break;
    case 'start-tour': startTour(); break;
    case 'update-progress': {
      renderUpdateProgress(msg);
      const cb = document.getElementById('cancelDownloadBtn');
      if (cb) cb.disabled = false;
      break;
    }
    case 'factory-reset-done': {
      const st = document.getElementById('factoryResetStatus');
      if (st) st.textContent = '已恢复出厂设置，正在自动重启…';
      showToast('已恢复出厂设置，正在重启…', 'ok');
      break;
    }
    case 'factory-reset-cancelled': {
      const st = document.getElementById('factoryResetStatus');
      if (st) st.textContent = '已取消';
      break;
    }
    case 'diagnostics-exported': {
      const dst = document.getElementById('diagnosticsStatus');
      if (dst) dst.textContent = msg.ok ? ('已导出到：' + (msg.path || '')) : (msg.message || '导出失败');
      showToast(msg.ok ? (msg.message || '诊断包已导出') : (msg.message || '诊断包导出失败'), msg.ok ? 'ok' : 'error');
      break;
    }
    case 'seewo-version-changed':
      showToast('检测到希沃客户端已更新，如遇登录异常请反馈', 'warn');
      break;
  }
}

// ===== 版本 / 更新检查 =====
function setVersion(version) {
  if (!version) return;
  const el = document.getElementById('appVersion');
  if (el) el.textContent = version;
}
function checkUpdate() {
  ['updateStatus','settingsUpdateStatus'].forEach(id => {
    const el = document.getElementById(id);
    if (el) { el.textContent = '正在检查更新…'; el.className = 'update-status'; }
  });
  const btn = document.getElementById('checkUpdateBtn');
  if (btn) btn.disabled = true;
  send({type:'check-update'});
  // 手动检查时给出反馈超时兜底
  setTimeout(function() {
    const el = document.getElementById('updateStatus');
    if (el && el.textContent === '正在检查更新…') {
      el.textContent = '检查超时，请检查网络或稍后重试';
      el.className = 'update-status error';
      if (btn) btn.disabled = false;
    }
  }, 20000);
}
function openUpdatePage() { send({type:'download-update'}); }
function fmtMB(bytes) { return ((bytes || 0) / 1048576).toFixed(1) + ' MB'; }
// ===== 下载完成后自动安装 =====
// 「关于」页与「设置 → 更新」各有一个开关，两处始终同步到同一份配置（autoInstallAfterDownload）
function setAutoInstallSwitch(enabled) {
  ['autoInstallUpdateCheck','autoInstallUpdateSettingsCheck'].forEach(function (id) {
    const el = document.getElementById(id);
    if (el) el.checked = !!enabled;
  });
}
function onAutoInstallSwitchChanged(enabled) {
  setAutoInstallSwitch(enabled);
  send({ type: 'update-setting', key: 'autoInstallAfterDownload', value: !!enabled });
  showToast(enabled ? '下载完成并通过校验后将自动静默安装' : '已关闭自动安装，更新包将只下载、需手动安装', 'info');
}
function renderUpdateProgress(msg) {
  msg = msg || {};
  const state = msg.state || '';
  let text = msg.text || '';
  if (state === 'downloading') {
    const total = msg.total || -1;
    text = total > 0
      ? '正在下载 ' + Math.round((msg.received || 0) * 100 / total) + '%（' + fmtMB(msg.received) + ' / ' + fmtMB(total) + '）'
      : '正在下载 ' + fmtMB(msg.received);
  }
  const installing = (state === 'verifying' || state === 'installing');
  ['updateStatus','settingsUpdateStatus'].forEach(function (id) {
    const el = document.getElementById(id);
    if (el) {
      el.textContent = text;
      el.className = 'update-status' + (state === 'error' ? ' error' : (state === 'installing' ? ' ok' : ''));
    }
  });
  const busy = (state === 'preparing' || state === 'downloading');
  const btn = document.getElementById('downloadUpdateBtn');
  if (btn) {
    btn.disabled = busy || installing;
    // 校验 / 静默安装阶段已经没什么可下载的了，别再让用户以为还能再点一次
    if (installing) btn.style.display = 'none';
  }
  // 下载过程中给出取消入口，避免用户只能干等
  const cancelBtn = document.getElementById('cancelDownloadBtn');
  if (cancelBtn) cancelBtn.style.display = busy ? '' : 'none';
}
bindClick('cancelDownloadBtn', function () {
  send({ type: 'cancel-download' });
  const cancelBtn = document.getElementById('cancelDownloadBtn');
  if (cancelBtn) cancelBtn.disabled = true;
  showToast('正在取消下载…', 'info');
});
function renderUpdateStatus(msg) {
  const text = msg.text || '';
  const kind = msg.state || '';
  ['updateStatus','settingsUpdateStatus'].forEach(id => {
    const el = document.getElementById(id);
    if (el) { el.textContent = text; el.className = 'update-status ' + kind; }
  });
  const btn = document.getElementById('checkUpdateBtn');
  if (btn) btn.disabled = false;
  const dl = document.getElementById('downloadUpdateBtn');
  if (dl) dl.style.display = msg.downloadUrl || msg.pageUrl ? '' : 'none';
  window.__updateUrl = msg.downloadUrl || msg.pageUrl || '';
  const badge = document.getElementById('updateBadge');
  if (badge) {
    if (msg.hasUpdate && msg.latest) {
      badge.textContent = '发现新版本 v' + String(msg.latest).replace(/^v/i, '');
      badge.style.display = '';
    } else if (msg.state === 'ok') {
      badge.style.display = 'none';
    }
  }
}

// ===== FAB：新增账号（加号旋转 45° + 二级菜单）=====
function toggleFab(e) {
  if (e) e.stopPropagation();
  const group = document.getElementById('fabGroup');
  const btn = document.getElementById('fabBtn');
  if (!group || !btn) return;
  const open = !group.classList.contains('open');
  group.classList.toggle('open', open);
  btn.classList.toggle('open', open);
  btn.setAttribute('aria-expanded', open ? 'true' : 'false');
}
function closeFab() {
  const group = document.getElementById('fabGroup');
  const btn = document.getElementById('fabBtn');
  if (group) group.classList.remove('open');
  if (btn) { btn.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); }
}
function fabNavigate(page) { closeFab(); navigate(page); }
document.addEventListener('click', function(e) {
  // 教程进行中由教程自己控制加号菜单的展开状态，避免点「下一步」时被这里顺手收起
  if (window.__tourActive) return;
  const group = document.getElementById('fabGroup');
  if (group && group.classList.contains('open') && !group.contains(e.target)) closeFab();
});
document.addEventListener('keydown', function(e) { if (e.key === 'Escape') closeFab(); });

// ===== Status Bar =====
function updateStatusBar() {
  const total = (state.accounts||[]).length;
  const active = Math.min(total, maxVisible());
  const el = document.getElementById('accountCountText');
  if (el) el.textContent = total > 0 ? `${active}/${total}` : '';
}

// ===== Two-Column Render =====
let accountListRendered = false;

function renderAccounts() {
  const accounts = state.accounts || [];
  const empty = document.getElementById('emptyState');
  const activeCol = document.getElementById('activeList');
  const inactiveCol = document.getElementById('inactiveList');
  const limit = maxVisible();
  const full = accounts.length >= limit;
  updateLimitHint();
  renderTagFilter();

  if (accounts.length === 0) {
    activeCol.innerHTML = ''; inactiveCol.innerHTML = '';
    setEmptyState(true, '暂无账号'); return;
  }

  // 生效区 / 未生效区的切分始终按后端下发的真实顺序算，
  // 标签筛选只决定「哪些卡片显示出来」，不会改变谁在生效区、谁排第几。
  const active = filterByTag(accounts.slice(0, limit));
  const inactive = filterByTag(accounts.slice(limit));

  if (active.length === 0 && inactive.length === 0) {
    activeCol.innerHTML = ''; inactiveCol.innerHTML = '';
    setEmptyState(true, '没有带「' + state.filterTag + '」标签的账号');
    return;
  }
  setEmptyState(false, '');

  document.getElementById('activeCount').textContent = active.length;
  document.getElementById('inactiveCount').textContent = inactive.length;

  // 入场动画只在列表首次出现时播放。
  // 之前每次 renderAccounts() 都会重建卡片 DOM，于是点选账号、切换生效区这类操作
  // 也会让整列卡片重新"飞入"一遍，看起来像在闪烁。
  const animate = !accountListRendered;
  accountListRendered = true;

  activeCol.innerHTML = active.map((a,i) => cardHtml(a, true, i, full, animate)).join('');
  inactiveCol.innerHTML = inactive.map((a,i) => cardHtml(a, false, i, full, animate)).join('');

  updateActionBar();
}

// ===== 标签筛选（只影响显示，后端数据与生效区顺序都不变）=====
function accountTags(a) {
  if (!a || !a.tags) return [];
  const raw = Array.isArray(a.tags) ? a.tags : String(a.tags).split(/[,，;；]/);
  return raw.map(function(t) { return String(t).trim(); }).filter(function(t) { return t.length > 0; });
}
function filterByTag(list) {
  const tag = state.filterTag;
  if (!tag) return list || [];
  return (list || []).filter(function(a) { return accountTags(a).indexOf(tag) >= 0; });
}
function collectTags() {
  const tags = [];
  (state.accounts || []).forEach(function(a) {
    accountTags(a).forEach(function(t) {
      if (!tags.some(function(x) { return x.toLowerCase() === t.toLowerCase(); })) tags.push(t);
    });
  });
  return tags;
}
function setEmptyState(show, text) {
  const empty = document.getElementById('emptyState');
  if (!empty) return;
  empty.style.display = show ? 'block' : 'none';
  if (show && text) {
    const t = empty.querySelector('.empty-text');
    if (t) t.textContent = text;
  }
}
function setTagFilter(tag) {
  state.filterTag = tag || '';
  renderAccounts();
}
// 下拉框选项只在标签集合变化时重建：否则用户每选一次都会让下拉框收起、丢掉焦点
function renderTagFilter() {
  const sel = document.getElementById('tagFilter');
  const tags = collectTags();
  // 正在筛选的标签已经不存在了（删账号 / 改标签）就自动回到「全部」，免得列表一直是空的
  if (state.filterTag && !tags.some(function(t) { return t === state.filterTag; })) state.filterTag = '';

  if (sel) {
    const signature = tags.join('\u0001');
    if (sel.getAttribute('data-tags') !== signature) {
      sel.innerHTML = '<option value="">全部标签</option>'
        + tags.map(function(t) { return '<option value="' + escAttr(t) + '">' + esc(t) + '</option>'; }).join('');
      sel.setAttribute('data-tags', signature);
    }
    const cur = state.filterTag || '';
    sel.value = cur;
    if (sel.value !== cur) sel.value = '';
    sel.disabled = tags.length === 0;
    sel.classList.toggle('active', !!cur);
  }

  const clearBtn = document.getElementById('tagFilterClear');
  if (clearBtn) clearBtn.style.display = state.filterTag ? '' : 'none';

  updateTagFilterHint();
}
function updateTagFilterHint() {
  const hint = document.getElementById('tagFilterHint');
  if (!hint) return;
  const tag = state.filterTag;
  if (!tag) { hint.style.display = 'none'; hint.innerHTML = ''; return; }
  const total = (state.accounts || []).length;
  const shown = filterByTag(state.accounts || []).length;
  hint.innerHTML = '<span>当前筛选：<b>' + esc(tag) + '</b> · 显示 ' + shown + ' / ' + total + ' 个账号</span>'
    + '<span style="flex:1"></span>'
    + '<button type="button" class="tag-filter-clear-btn" id="tagFilterClearBtn">清除筛选</button>';
  hint.style.display = 'flex';
  bindClick('tagFilterClearBtn', function() { setTagFilter(''); });
}

function cardHtml(a, isActive, idx, isFull, animate) {
  const sel = state.selectedId === a.id ? ' selected' : '';
  const freq = a.requestCount > 0 ? '<span class="freq" title="最近SSO请求: ' + escAttr(a.lastRequestAtUtc || '-') + '">' + esc(a.requestCount) + '次</span>' : '';
  // 健康巡检小圆点：优先使用 account-list 里持久化的 healthState（刷新/重启后仍显示），
  // 回退到本次巡检消息缓存的 state.health（无巡检数据时不渲染）
  const cached = state.health ? state.health[a.id] : null;
  const hState = a.healthState || (cached && cached.state) || '';
  const hMsg = a.healthMessage || (cached && cached.message) || '';
  const healthDot = hState ? '<span class="health-dot ' + healthLevel(hState) + '" title="' + escAttr(hMsg) + '"></span>' : '';
  const switchBtn = isActive
    ? `<button class="switch-btn" data-switch="inactive" title="移到未生效">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 4l-1.41 1.41L16.17 11H4v2h12.17l-5.58 5.59L12 20l8-8z"/></svg>
      </button>`
    : (isFull
        ? `<button class="switch-btn disabled" disabled title="正在生效账号位已满">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 4l1.41 1.41L7.83 11H20v2H7.83l5.58 5.59L12 20l-8-8z"/></svg>
          </button>`
        : `<button class="switch-btn" data-switch="active" title="移到生效">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 4l1.41 1.41L7.83 11H20v2H7.83l5.58 5.59L12 20l-8-8z"/></svg>
          </button>`);
  // 后台自动续期状态：让「这个账号有没有被自动刷新过」一眼可见，
  // 而不是只能靠账号突然不可用来发现问题。占位账号不参与续期，不显示。
  let renewLine = '';
  if (!a.isPlaceholder && a.lastTokenExchangeAtUtc) {
    const failed = hState === 'bad';
    renewLine = '<div class="renew-line' + (failed ? ' bad' : '') + '">'
      + (failed ? '续期失败 · ' : '已自动续期 · ')
      + esc(relativeTime(a.lastTokenExchangeAtUtc))
      + '</div>';
  }

  // 扫码账号的凭据一旦过期就无法自动恢复（希沃只发一个 token，没有 refresh token），
  // 所以直接给一个补救入口：重新扫一次，登录后会自动替换该账号的凭据，不用删了重加。
  const rescanBtn = (!a.isPlaceholder && a.loginType === '扫码')
    ? `<button class="switch-btn rescan${hState === 'bad' ? ' urgent' : ''}" data-rescan="${escAttr(a.id)}"
         title="重新扫码：扫码登录后会自动替换该账号的凭据">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M17.65 6.35A7.96 7.96 0 0 0 12 4a8 8 0 1 0 7.73 10h-2.08A6 6 0 1 1 12 6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/></svg>
      </button>`
    : '';

  return `<div class="acct-card${sel}${animate ? ' enter' : ''}" data-id="${escAttr(a.id)}">
    <div class="row1">
      <div class="avatar-mini"><span>${esc(a.initial||'S')}</span></div>
      <div class="info">
        <div class="name">${esc(a.displayName||a.username||'')}</div>
        <div class="meta">${esc(a.username||'')} · ${esc(a.loginType||'')}</div>
        ${renewLine}
      </div>
      ${healthDot}
      ${freq}
      ${rescanBtn}
      ${switchBtn}
    </div>
  </div>`;
}

// 把时间戳转成人话（刚刚 / N 分钟前 / N 小时前 / N 天前）
function relativeTime(text) {
  const t = new Date(String(text).replace(' ', 'T'));
  if (isNaN(t.getTime())) return text;
  const sec = (Date.now() - t.getTime()) / 1000;
  if (sec < 60) return '刚刚';
  if (sec < 3600) return Math.floor(sec / 60) + ' 分钟前';
  if (sec < 86400) return Math.floor(sec / 3600) + ' 小时前';
  return Math.floor(sec / 86400) + ' 天前';
}

// 重新扫码：走的是和「扫码登录」完全相同的流程。
// C# 侧扫码成功后会按账号匹配已有账号并替换其凭据（App.CompleteQrLoginAsync 里已有该逻辑），
// 所以这里不需要特殊参数，扫同一个账号即可。
function rescanAccount(id) {
  const a = (state.accounts || []).find(x => x.id === id);
  navigate('qr');
  send({ type: 'start-qr' });
  showToast('请用希沃 App 扫码登录' + (a && a.displayName ? '（' + a.displayName + '）' : '') + '，登录成功后会自动替换该账号的凭据', 'info');
}

function switchToActive(id) {
  send({type:'move-to-active', id});
}
function switchToInactive(id) {
  send({type:'move-to-inactive', id});
}

function selectAccount(id) {
  state.selectedId = state.selectedId === id ? null : id;
  // 只改选中态，不重建整个列表 —— 重建会让所有卡片重新插入 DOM，看着像整列刷新了一遍
  document.querySelectorAll('.acct-card').forEach(function (el) {
    el.classList.toggle('selected', el.getAttribute('data-id') === state.selectedId);
  });
  updateActionBar();
}

function updateActionBar() {
  const sel = state.selectedId;
  const has = !!sel && (state.accounts||[]).some(a => a.id === sel);
  ['actEditName','actEditTags','actSetActive','actDelete','actMoveUp','actMoveDown'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.disabled = !has;
  });
  if (has) {
    const acct = state.accounts.find(a => a.id === sel);
    if (acct) {
      const setActiveBtn = document.getElementById('actSetActive');
      if (setActiveBtn) setActiveBtn.style.display = acct.isActive ? 'none' : '';
    }
  }
}

// ===== Action Handlers =====
function actEditName() {
  if (!state.selectedId) return;
  const acct = state.accounts.find(a => a.id === state.selectedId);
  // WebView2 不支持 window.prompt（点了不会弹任何东西，静默失败），交给 C# 用原生输入框
  send({ type: 'rename-account-dialog', id: acct ? acct.id : '' });
  const name = null;   // 下面原有的校验逻辑保留，但不会再走到
  if (name && name.trim()) send({type:'edit-display-name', id:state.selectedId, name:name.trim()});
}
function actEditTags() {
  if (!state.selectedId) return;
  // 标签输入交给 C# 侧的原生输入框：WebView2 不支持 window.prompt，
  // 用 prompt 会出现「点了『标签』毫无反应」的静默失败。
  send({type:'edit-tags-dialog', id:state.selectedId});
}
function actSetActive() {
  if (!state.selectedId) return;
  send({type:'set-active', id:state.selectedId});
}
function actDelete() {
  if (!state.selectedId) return;
  const acct = state.accounts.find(a => a.id === state.selectedId);
  if (confirm(`确定删除账号 "${acct ? (acct.displayName || acct.username) : ''}" 吗？`))
    send({type:'delete-account', id:state.selectedId});
}

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

// ===== Settings =====
document.addEventListener('change', function(e) {
  switch(e.target.id) {
    case 'usePasswordCheck': send({type:'update-setting', key:'usePluginPassword', value:e.target.checked}); const pp = document.getElementById('passwordPanel'); if(pp) pp.style.display = e.target.checked?'block':'none'; break;
    case 'rotationEnabledCheck': send({type:'update-setting', key:'userListRotationEnabled', value:e.target.checked}); break;
    case 'autoStartCheck': send({type:'update-setting', key:'autoStart', value:e.target.checked}); break;
    case 'minimizeToTrayCheck': send({type:'update-setting', key:'minimizeToTray', value:e.target.checked}); break;
    case 'startMinimizedCheck': send({type:'update-setting', key:'startMinimized', value:e.target.checked}); break;
    case 'autoShowOverlayCheck': send({type:'update-setting', key:'autoShowOverlay', value:e.target.checked}); break;
    case 'autoCheckUpdateCheck': send({type:'update-setting', key:'autoCheckUpdate', value:e.target.checked}); break;
    case 'autoInstallUpdateCheck': onAutoInstallSwitchChanged(e.target.checked); break;
    case 'autoInstallUpdateSettingsCheck': onAutoInstallSwitchChanged(e.target.checked); break;
    case 'rotationGroupSize': send({type:'update-setting', key:'userListRotationGroupSize', value:parseInt(e.target.value)}); break;
    case 'tagFilter': setTagFilter(e.target.value); break;
    case 'particleToggle': toggleParticles(e.target.checked); break;
  }
});
function setPassword() { const p = document.getElementById('newPasswordInput').value; if(p){send({type:'set-password',password:p});document.getElementById('newPasswordInput').value='';} }
function clearPassword() { send({type:'clear-password'}); }

// ===== Lock =====
function showLock() { document.getElementById('lockOverlay').style.display = 'flex'; }
function hideLock() { document.getElementById('lockOverlay').style.display = 'none'; }
function unlockApp() { send({type:'unlock', password:document.getElementById('unlockPasswordInput').value}); }
const unlockInput = document.getElementById('unlockPasswordInput');
if (unlockInput) unlockInput.addEventListener('keydown', function(e) { if (e.key === 'Enter') unlockApp(); });

// ===== Debug =====
function addFakeAccount() {
  // 同上：prompt 在 WebView2 里无效，改由 C# 弹原生输入框
  send({ type: 'add-fake-account-dialog' });
}

// ===== Particle Background =====
let particleCanvas = null, pCtx = null, pAniId = null, pParticles = [];
function initParticles() {
  if (document.getElementById('particleCanvas')) return;
  const canvas = document.createElement('canvas');
  canvas.id = 'particleCanvas';
  canvas.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:0';
  document.body.appendChild(canvas);
  particleCanvas = canvas;
  pCtx = canvas.getContext('2d');
  resizeParticles();
  window.addEventListener('resize', resizeParticles);
  if (state.particles !== false) startParticles();
}
function resizeParticles() {
  if (!particleCanvas) return;
  particleCanvas.width = window.innerWidth;
  particleCanvas.height = window.innerHeight;
}
function startParticles() {
  if (pAniId) return;
  const count = Math.min(80, Math.floor((particleCanvas.width * particleCanvas.height) / 15000));
  pParticles = Array.from({length:count}, () => ({
    x: Math.random() * particleCanvas.width,
    y: Math.random() * particleCanvas.height,
    vx: (Math.random() - 0.5) * 0.5,
    vy: (Math.random() - 0.5) * 0.5,
    r: Math.random() * 1.5 + 0.5
  }));
  function loop() {
    if (!pCtx || !particleCanvas) return;
    pCtx.clearRect(0, 0, particleCanvas.width, particleCanvas.height);
    for (let p of pParticles) {
      p.x += p.vx; p.y += p.vy;
      if (p.x < 0 || p.x > particleCanvas.width) p.vx *= -1;
      if (p.y < 0 || p.y > particleCanvas.height) p.vy *= -1;
      pCtx.beginPath();
      pCtx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      pCtx.fillStyle = 'rgba(255,255,255,0.4)';
      pCtx.fill();
    }
    // 连线
    for (let i = 0; i < pParticles.length; i++) {
      for (let j = i + 1; j < pParticles.length; j++) {
        const dx = pParticles[i].x - pParticles[j].x;
        const dy = pParticles[i].y - pParticles[j].y;
        const dist = Math.sqrt(dx * dx + dy * dy);
        if (dist < 150) {
          pCtx.beginPath();
          pCtx.moveTo(pParticles[i].x, pParticles[i].y);
          pCtx.lineTo(pParticles[j].x, pParticles[j].y);
          pCtx.strokeStyle = `rgba(255,255,255,${0.12 * (1 - dist / 150)})`;
          pCtx.lineWidth = 0.5;
          pCtx.stroke();
        }
      }
    }
    pAniId = requestAnimationFrame(loop);
  }
  loop();
}
function stopParticles() {
  if (pAniId) { cancelAnimationFrame(pAniId); pAniId = null; }
  if (pCtx) pCtx.clearRect(0, 0, particleCanvas.width, particleCanvas.height);
}
function toggleParticles(on) {
  state.particles = on;
  if (on) startParticles(); else stopParticles();
}
// 页面加载完自动初始化
if (document.readyState === 'complete') initParticles();
else window.addEventListener('load', initParticles);

// ===== Helpers =====
function esc(s) { if(!s) return ''; return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
function escAttr(s) { return esc(s).replace(/'/g, '&#39;'); }

// ===== 事件委托：账号卡片 / 生效切换（替代内联 onclick 字符串拼接，避免单引号注入）=====
document.addEventListener('click', function(e) {
  const t = e.target;
  if (!t || !t.closest) return;
  const sw = t.closest('.switch-btn');
  if (sw) {
    const card = sw.closest('.acct-card');
    const id = card ? card.getAttribute('data-id') : '';
    if (!id || sw.disabled || sw.classList.contains('disabled')) return;
    // 重新扫码按钮同样带 .switch-btn（沿用同一套样式），所以要先判断它
    if (sw.hasAttribute('data-rescan')) { rescanAccount(id); return; }
    if (sw.getAttribute('data-switch') === 'inactive') switchToInactive(id);
    else switchToActive(id);
    return;
  }
  const card = t.closest('.acct-card');
  if (card && card.parentNode && (card.parentNode.id === 'activeList' || card.parentNode.id === 'inactiveList')) {
    const id = card.getAttribute('data-id');
    if (id) selectAccount(id);
  }
});

// 上移 / 下移：读 state.selectedId（修复原内联 selectedId ReferenceError）
function moveSelected(dir) {
  const id = state.selectedId;
  if (!id) return;
  if (!(state.accounts || []).some(function(a) { return a.id === id; })) return;
  send({ type: dir < 0 ? 'move-up' : 'move-down', id: id });
}

// ===== Toast（右下角轻提示，2.5s 自动消失）=====
function toastLevelClass(level) {
  const l = String(level || '').toLowerCase();
  if (l === 'error' || l === 'err' || l === 'danger' || l === 'failed' || l === 'fail') return 'toast-error';
  if (l === 'warn' || l === 'warning') return 'toast-warn';
  if (l === 'ok' || l === 'success') return 'toast-ok';
  return 'toast-info';
}
function showToast(text, level) {
  if (text === undefined || text === null || text === '') return;
  let box = document.getElementById('toastContainer');
  if (!box) {
    box = document.createElement('div');
    box.id = 'toastContainer';
    box.className = 'toast-container';
    document.body.appendChild(box);
  }
  const el = document.createElement('div');
  el.className = 'toast ' + toastLevelClass(level);
  el.textContent = String(text);
  box.appendChild(el);
  setTimeout(function() {
    el.classList.add('hide');
    setTimeout(function() { if (el.parentNode) el.parentNode.removeChild(el); }, 260);
  }, 2500);
}

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
  setCheckItem('seewo', seewoLevel, '希沃进程', seewoLevel === 'unknown' ? '' : (st.seewoRunning ? '' : '未运行'));

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
function healthLevel(s) { return s === 'ok' ? 'ok' : (s === 'bad' ? 'bad' : 'unknown'); }
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

// ===== 批量导入 =====
let batchTimer = null;
function setBatchBusy(busy) {
  const btn = document.getElementById('batchImportBtn');
  if (!btn) return;
  btn.disabled = !!busy;
  btn.textContent = busy ? '导入中…' : '批量导入';
}
function batchImport() {
  const ta = document.getElementById('batchImportText');
  const res = document.getElementById('batchImportResult');
  const text = ta ? ta.value : '';
  if (!text || !text.trim()) {
    if (res) res.innerHTML = '<div class="batch-line err">请先输入要导入的账号，每行一个</div>';
    showToast('请先输入要导入的账号', 'warn');
    return;
  }
  if (res) res.innerHTML = '';
  send({ type: 'batch-import', text: text });
  setBatchBusy(true);
  if (batchTimer) clearTimeout(batchTimer);
  batchTimer = setTimeout(function() {
    batchTimer = null;
    setBatchBusy(false);
    showToast('批量导入超时，请重试', 'warn');
  }, 30000);
}
function batchLineLevel(text) {
  return /失败|错误|无效|已存在|重复|格式|缺少|无法|跳过|fail|error|invalid|exist|skip/i.test(String(text || '')) ? 'err' : 'ok';
}
function renderBatchImportResult(msg) {
  msg = msg || {};
  if (batchTimer) { clearTimeout(batchTimer); batchTimer = null; }
  setBatchBusy(false);
  const res = document.getElementById('batchImportResult');
  const added = parseInt(msg.added, 10) || 0;
  const failed = parseInt(msg.failed, 10) || 0;
  if (res) {
    let html = '<div class="batch-line ' + (failed > 0 ? 'err' : 'ok') + '">导入完成：成功 ' + added + ' 个，失败 ' + failed + ' 个</div>';
    (msg.messages || []).forEach(function(m) {
      html += '<div class="batch-line ' + batchLineLevel(m) + '">' + esc(m) + '</div>';
    });
    res.innerHTML = html;
  }
  if (added > 0) showToast('批量导入完成：成功 ' + added + ' 个' + (failed > 0 ? '，失败 ' + failed + ' 个' : ''), failed > 0 ? 'warn' : 'ok');
  else if (failed > 0) showToast('批量导入失败 ' + failed + ' 个', 'error');
}


// ===== CSV 文件导入 =====
// 文件选择在 C# 侧弹系统对话框，用户挑文件的时间也算在等待里，所以超时给得比粘贴导入宽得多
let csvTimer = null;
const CSV_IMPORT_TIMEOUT = 120000;
const CSV_FAIL_LINES_SHOWN = 5;

function setCsvBusy(busy) {
  const btn = document.getElementById('importCsvBtn');
  if (!btn) return;
  btn.disabled = !!busy;
  btn.textContent = busy ? '等待选择文件…' : '选择 CSV 文件导入';
}

function importCsvFile() {
  const res = document.getElementById('batchImportResult');
  if (res) res.innerHTML = '';
  send({ type: 'import-csv' });
  setCsvBusy(true);
  showToast('请在弹窗里选择要导入的 CSV 文件', 'info');
  if (csvTimer) clearTimeout(csvTimer);
  csvTimer = setTimeout(function() {
    csvTimer = null;
    setCsvBusy(false);
    showToast('CSV 导入超时，请重试', 'warn');
  }, CSV_IMPORT_TIMEOUT);
}

function renderCsvImportResult(msg) {
  msg = msg || {};
  if (csvTimer) { clearTimeout(csvTimer); csvTimer = null; }
  setCsvBusy(false);

  if (msg.cancelled) { showToast('已取消 CSV 导入', 'info'); return; }

  const added = parseInt(msg.added, 10) || 0;
  const updated = parseInt(msg.updated, 10) || 0;
  const failed = parseInt(msg.failed, 10) || 0;
  const fails = (msg.messages || []).filter(function(m) { return !!m; });

  const res = document.getElementById('batchImportResult');
  if (res) {
    let html = '<div class="batch-line ' + (failed > 0 ? 'err' : 'ok') + '">CSV 导入完成：新增 '
      + added + ' 个，更新 ' + updated + ' 个，失败 ' + failed + ' 个</div>';
    // 明细只列前几条失败原因：整份失败清单对用户没用，还容易把区域撑得很长
    fails.slice(0, CSV_FAIL_LINES_SHOWN).forEach(function(m) {
      html += '<div class="batch-line err">' + esc(m) + '</div>';
    });
    if (fails.length > CSV_FAIL_LINES_SHOWN) {
      html += '<div class="batch-line err">…还有 ' + (fails.length - CSV_FAIL_LINES_SHOWN) + ' 条失败原因未显示</div>';
    }
    res.innerHTML = html;
  }

  if (added + updated > 0) {
    showToast('CSV 导入完成：新增 ' + added + ' 个，更新 ' + updated + ' 个'
      + (failed > 0 ? '，失败 ' + failed + ' 个' : ''), failed > 0 ? 'warn' : 'ok');
  } else if (failed > 0) {
    showToast('CSV 导入失败 ' + failed + ' 个，详见导入明细', 'error');
  } else {
    showToast('CSV 文件里没有可导入的账号', 'warn');
  }
}

// ===== 配置备份与还原 =====
let backupsTimer = null;
function listBackups() {
  const btn = document.getElementById('listBackupsBtn');
  const list = document.getElementById('backupList');
  send({ type: 'list-backups' });
  state.backupsLoading = true;
  if (btn) { btn.disabled = true; btn.textContent = '加载中…'; }
  if (list) list.innerHTML = '<div class="backup-empty">正在加载备份列表…</div>';
  if (backupsTimer) clearTimeout(backupsTimer);
  backupsTimer = setTimeout(function() {
    backupsTimer = null;
    if (!state.backupsLoading) return;
    state.backupsLoading = false;
    const b = document.getElementById('listBackupsBtn');
    if (b) { b.disabled = false; b.textContent = '查看备份'; }
    const l = document.getElementById('backupList');
    if (l) l.innerHTML = '<div class="backup-empty">加载超时，请重试</div>';
  }, 15000);
}
function renderBackups(msg) {
  msg = msg || {};
  if (backupsTimer) { clearTimeout(backupsTimer); backupsTimer = null; }
  state.backupsLoading = false;
  const btn = document.getElementById('listBackupsBtn');
  if (btn) { btn.disabled = false; btn.textContent = '查看备份'; }
  const list = document.getElementById('backupList');
  if (!list) return;
  const items = msg.items || [];
  if (!items.length) { list.innerHTML = '<div class="backup-empty">暂无备份</div>'; return; }
  list.innerHTML = items.map(function(it) {
    it = it || {};
    const name = it.name || '';
    const accounts = (it.accounts === undefined || it.accounts === null) ? '' : ((parseInt(it.accounts, 10) || 0) + ' 个账号');
    return '<div class="backup-item">' +
      '<div class="backup-info">' +
        '<div class="backup-time">' + esc(it.time || '未知时间') + '</div>' +
        '<div class="backup-file">' + esc(name) + (accounts ? ' · ' + esc(accounts) : '') + '</div>' +
      '</div>' +
      '<button class="btn btn-secondary btn-small backup-restore" data-name="' + escAttr(name) + '">恢复</button>' +
    '</div>';
  }).join('');
}
function restoreBackup(name) {
  if (!name) return;
  if (!confirm('确定恢复备份 “' + name + '” 吗？当前配置将被覆盖，此操作不可撤销。')) return;
  send({ type: 'restore-backup', name: name });
  showToast('正在恢复备份…', 'info');
}
document.addEventListener('click', function(e) {
  const btn = (e.target && e.target.closest) ? e.target.closest('.backup-restore') : null;
  if (!btn) return;
  restoreBackup(btn.getAttribute('data-name') || '');
});

// ===== 静态控件绑定（不使用内联 onclick 拼接）=====
function bindClick(id, fn) {
  const el = document.getElementById(id);
  if (el) el.addEventListener('click', fn);
}
bindClick('actMoveUp', function() { moveSelected(-1); });
bindClick('actMoveDown', function() { moveSelected(1); });

// ===== 使用教程（聚光灯引导）=====
// 动效约定：遮罩用 clip-path 挖洞（GPU），光圈用 transform + 尺寸过渡，
// 卡片与图片只动 transform/opacity；总时长与 styles.css 中的过渡保持一致。
const TOUR_PAD = 10;
const TOUR_STEPS = [
  {
    step: '第 1 步 · 共 5 步',
    title: '平时从任务栏托盘打开它',
    html: '软件启动后会缩到右下角托盘。双击那个<b>蓝色闪电图标</b>（或右键它）就能随时打开这个管理界面。',
    media: 'assets/tutorial-tray.png',
    mediaClass: 'tray'
  },
  {
    step: '第 2 步 · 共 5 步',
    title: '方式一：右下角的 + 号',
    html: '点右下角的 <b>+ 号</b>会展开两个入口：<b>密码添加</b>（账号密码登录）和 <b>扫码添加</b>（希沃 App 扫码）。',
    focus: '#fabBtn, #fabMenu',
    openFab: true
  },
  {
    step: '第 3 步 · 共 5 步',
    title: '方式二：左侧侧边栏',
    html: '左侧的 <b>添加账号</b> 与 <b>扫码登录</b> 是同样的两个入口，挑顺手的一种用就行。',
    focus: '#navAdd, #navQr'
  },
  {
    step: '第 4 步 · 共 5 步',
    title: '添加后，希沃会变成这样',
    html: '打开希沃白板的登录界面，账号头像会直接排在这里，<b>点一下头像就能登录</b>，不用再输账号密码。',
    media: 'assets/tutorial-seewo.png',
    mediaClass: 'seewo',
    wide: true
  },
  {
    step: '第 5 步 · 共 5 步',
    title: '可以开始用了',
    html: '教程随时能在「<b>设置 → 帮助与维护</b>」里重看一遍。',
    last: true
  }
];

let tourIndex = -1;
let tourTimer = null;

function tourEl(id) { return document.getElementById(id); }

function tourUnionRect(selector) {
  const els = document.querySelectorAll(selector);
  let box = null;
  for (let i = 0; i < els.length; i++) {
    const r = els[i].getBoundingClientRect();
    if (r.width === 0 && r.height === 0) continue;
    if (!box) box = { top: r.top, left: r.left, right: r.right, bottom: r.bottom };
    else {
      box.top = Math.min(box.top, r.top);
      box.left = Math.min(box.left, r.left);
      box.right = Math.max(box.right, r.right);
      box.bottom = Math.max(box.bottom, r.bottom);
    }
  }
  return box;
}

// 把聚光灯（遮罩挖洞 + 发光光圈）移到目标上；selector 为空表示全屏遮罩
function tourSpotlight(selector) {
  const mask = tourEl('tourMask'), ring = tourEl('tourRing'), card = tourEl('tourCard');
  if (!mask || !ring || !card) return;
  const vw = window.innerWidth, vh = window.innerHeight;
  const box = selector ? tourUnionRect(selector) : null;

  if (!box) {
    mask.style.setProperty('--tour-top', '-20px');
    mask.style.setProperty('--tour-right', '-20px');
    mask.style.setProperty('--tour-bottom', '-20px');
    mask.style.setProperty('--tour-left', '-20px');
    ring.classList.remove('show');
    tourPlaceCard(null);
    return;
  }

  const top = Math.max(0, box.top - TOUR_PAD);
  const left = Math.max(0, box.left - TOUR_PAD);
  const right = Math.max(0, vw - box.right - TOUR_PAD);
  const bottom = Math.max(0, vh - box.bottom - TOUR_PAD);

  mask.style.setProperty('--tour-top', top + 'px');
  mask.style.setProperty('--tour-right', right + 'px');
  mask.style.setProperty('--tour-bottom', bottom + 'px');
  mask.style.setProperty('--tour-left', left + 'px');

  ring.style.width = (vw - left - right) + 'px';
  ring.style.height = (vh - top - bottom) + 'px';
  ring.style.transform = 'translate3d(' + left + 'px,' + top + 'px,0)';
  ring.classList.add('show');

  tourPlaceCard({ top: top, right: vw - right, bottom: vh - bottom, left: left });
}

// 卡片优先贴在洞下方；放不下就翻到上方，再不行就居中
function tourPlaceCard(hole) {
  const card = tourEl('tourCard');
  if (!card) return;
  const vw = window.innerWidth, vh = window.innerHeight;
  const cw = card.offsetWidth || 430;
  const ch = card.offsetHeight || 240;
  const gap = 16;

  let x = hole ? hole.left : (vw - cw) / 2;
  let y;

  if (!hole) {
    y = (vh - ch) / 2;
  } else if (hole.bottom + gap + ch <= vh - 16) {
    y = hole.bottom + gap;
  } else if (hole.top - gap - ch >= 16) {
    y = hole.top - gap - ch;
  } else {
    y = Math.max(16, (vh - ch) / 2);
  }

  // 卡片比视口还高时顶到最上面，宁可贴边也不要让「继续/完成」被裁掉
  if (ch > vh - 32) y = 16;

  x = Math.max(16, Math.min(x, vw - cw - 16));
  card.style.transform = 'translate3d(' + Math.round(x) + 'px,' + Math.round(y) + 'px,0)';
  card.classList.add('show');
}

function tourRenderStep(i) {
  tourIndex = i;
  const s = TOUR_STEPS[i];
  if (!s) return;

  const stepEl = tourEl('tourStep'), titleEl = tourEl('tourTitle'), textEl = tourEl('tourText');
  const media = tourEl('tourMedia'), card = tourEl('tourCard');
  const nextBtn = tourEl('tourNextBtn'), hint = tourEl('tourHint');
  if (!stepEl || !titleEl || !textEl || !media || !card) return;

  stepEl.textContent = s.step || '';
  titleEl.textContent = s.title || '';
  textEl.innerHTML = s.html || '';

  media.classList.remove('in');
  media.innerHTML = '';
  if (s.media) {
    const img = document.createElement('img');
    img.alt = '';
    if (s.mediaClass) img.className = s.mediaClass;
    img.addEventListener('load', function () { media.classList.add('in'); });
    img.src = s.media;
    media.appendChild(img);
  }

  card.classList.toggle('wide', !!s.wide);

  // 导航按钮：第一步不显示「上一步」，最后一步把「下一步」换成「完成教学」
  const prevBtn = tourEl('tourPrevBtn');
  if (prevBtn) prevBtn.style.display = i > 0 ? '' : 'none';
  if (nextBtn) nextBtn.textContent = s.last ? '完成教学' : '下一步';
  if (hint) {
    hint.textContent = s.last ? '' : '点击任意地方继续';
    hint.classList.toggle('pulse', !s.last);
  }

  // 只在需要展示加号菜单的步骤展开它，切走后自动收起
  const group = document.getElementById('fabGroup');
  if (group) group.classList.toggle('open', !!s.openFab);

  // 内容变了，先隐藏卡片量好尺寸，再定位淡入
  card.classList.remove('show');
  clearTimeout(tourTimer);
  requestAnimationFrame(function () {
    if (!s.focus) { tourSpotlight(null); return; }
    // 菜单有展开动画，等它稳定后再测量，否则框选范围会按收起状态计算
    tourTimer = setTimeout(function () { tourSpotlight(s.focus); }, s.openFab ? 520 : 60);
  });
}

function startTour() {
  const ov = tourEl('tourOverlay');
  if (!ov) return;

  // 加号悬浮按钮只属于账号列表页：在设置页直接开教程会框不到它（元素不参与布局）。
  // 教程统一在主界面（账号列表）演示，点「显示使用教程」时先切过去。
  if (typeof navigate === 'function') navigate('accounts');

  if (ov.hidden) ov.hidden = false;
  document.body.classList.add('tour-open');
  window.__tourActive = true;
  tourRenderStep(0);
  send({ type: 'tour-started' });
}

function tourNext() {
  if (tourIndex < 0) return;
  if (tourIndex >= TOUR_STEPS.length - 1) { endTour(true); return; }
  tourRenderStep(tourIndex + 1);
}

function tourPrev() {
  if (tourIndex <= 0) return;
  tourRenderStep(tourIndex - 1);
}

function endTour(completed) {
  clearTimeout(tourTimer);
  tourTimer = null;
  tourIndex = -1;
  window.__tourActive = false;

  const ov = tourEl('tourOverlay');
  if (ov) ov.hidden = true;
  document.body.classList.remove('tour-open');

  const ring = tourEl('tourRing');
  if (ring) ring.classList.remove('show');
  const card = tourEl('tourCard');
  if (card) card.classList.remove('show');
  const group = document.getElementById('fabGroup');
  if (group) group.classList.remove('open');

  send({ type: 'tour-done', completed: !!completed });
}

(function bindTour() {
  const ov = tourEl('tourOverlay');
  if (!ov) return;

  ov.addEventListener('click', function (e) {
    if (e.target && e.target.closest &&
        (e.target.closest('#tourSkipBtn') || e.target.closest('#tourNextBtn') || e.target.closest('#tourPrevBtn'))) return;
    tourNext();
  });
  bindClick('tourPrevBtn', function (e) {
    if (e && e.stopPropagation) e.stopPropagation();
    tourPrev();
  });
  bindClick('tourSkipBtn', function (e) {
    if (e && e.stopPropagation) e.stopPropagation();
    endTour(false);
  });
  bindClick('tourNextBtn', function (e) {
    if (e && e.stopPropagation) e.stopPropagation();
    // 每一步都显示「下一步」；到第 5 步时 tourNext() 内部会以“完成”收尾
    tourNext();
  });
  document.addEventListener('keydown', function (e) {
    if (tourIndex < 0) return;
    if (e.key === 'Escape') { e.preventDefault(); endTour(false); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); tourPrev(); }
    else if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowRight') { e.preventDefault(); tourNext(); }
  });
  window.addEventListener('resize', function () {
    if (tourIndex >= 0) tourRenderStep(tourIndex);
  });
})();

bindClick('startTourBtn', function () { startTour(); });
// ===== 用户协议：关于页点击后全屏弹窗展示 =====
// 正文由 C# 从嵌入资源下发（Resources/terms.txt），与欢迎界面的协议页共用同一份，避免两处内容不一致
function renderTerms(text) {
  const NL = String.fromCharCode(10);
  const body = document.getElementById('termsBody');
  if (!body) return;
  body.innerHTML = '';
  const frag = document.createDocumentFragment();
  (text || '').split(NL).forEach(function (raw) {
    const line = raw.trimEnd();
    const t = line.trim();
    const div = document.createElement('div');
    if (!t) { div.className = 't-gap'; }
    else if (t.indexOf('用户协议') === 0 && t.indexOf('希沃自动登录') === 0) { div.className = 't-title'; div.textContent = t; }
    else if (t.indexOf('版本 ') === 0 || t.indexOf('（本协议全文完）') === 0) { div.className = 't-meta'; div.textContent = t; }
    else if (/^(第[一二三四五六七八九十]+条|附：)/.test(t)) { div.className = 't-head'; div.textContent = t; }
    else if (/^【/.test(t)) { div.className = 't-warn'; div.textContent = t; }
    else if (/^[0-9]+[.][0-9]+/.test(t)) { div.className = 't-clause'; div.textContent = t; }
    else if (/^　/.test(line)) { div.className = 't-sub'; div.textContent = t; }
    else { div.className = 't-p'; div.textContent = t; }
    frag.appendChild(div);
  });
  body.appendChild(frag);
  body.scrollTop = 0;
}

function openTerms() {
  const ov = document.getElementById('termsOverlay');
  if (!ov) return;
  ov.hidden = false;
  if (window.__termsText) {
    renderTerms(window.__termsText);
  } else {
    renderTerms('正在载入协议正文…');
    send({ type: 'get-terms' });
  }
}

function closeTerms() {
  const ov = document.getElementById('termsOverlay');
  if (ov) ov.hidden = true;
}

// ===== 侧边栏滑动指示条 =====
// 让「当前在哪一页」有连续的空间感：切换时指示条滑过去，而不是硬切。
// ===== 侧边栏白色跟随条 =====
// 鼠标移到哪一项，白条就滑到哪；离开导航区时淡出。
function moveNavHover(target) {
  var nav = document.querySelector('nav');
  var hv = document.getElementById('navHover');
  if (!nav || !hv) return;
  if (!target) { hv.style.opacity = '0'; return; }
  var nr = nav.getBoundingClientRect();
  var tr = target.getBoundingClientRect();
  var hh = Math.min(tr.height * 0.44, 16);
  hv.style.height = hh + 'px';
  hv.style.opacity = '1';
  hv.style.transform = 'translateY(' + (tr.top - nr.top + (tr.height - hh) / 2) + 'px)';
}
function bindNavHover() {
  var nav = document.querySelector('nav');
  if (!nav) return;
  // 用 mousemove + closest 判断，而不是逐项 mouseenter：
  // 鼠标从导航项移到导航区的间隙（gap / padding）时并不会触发 mouseleave，
  // 那样白条就会停在原地不消失。
  nav.addEventListener('mousemove', function (e) {
    var el = e.target && e.target.closest ? e.target.closest('.nav-item') : null;
    moveNavHover(el);
  });
  nav.addEventListener('mouseleave', function () { moveNavHover(null); });
  // 鼠标移出窗口或窗口失去焦点时也收起来
  document.addEventListener('mouseleave', function () { moveNavHover(null); });
  window.addEventListener('blur', function () { moveNavHover(null); });
  // 切换页面时鼠标可能已经不在侧边栏，顺手收一次
  document.querySelectorAll('.nav-item').forEach(function (el) {
    el.addEventListener('click', function () { setTimeout(function () { moveNavHover(null); }, 260); });
  });
}

function moveNavIndicator() {
  var nav = document.querySelector('nav');
  var ind = document.getElementById('navIndicator');
  var active = document.querySelector('.nav-item.active');
  if (!nav || !ind) return;
  if (!active) { ind.style.opacity = '0'; return; }
  var nr = nav.getBoundingClientRect();
  var ar = active.getBoundingClientRect();
  var h = Math.min(ar.height * 0.5, 20);
  ind.style.height = h + 'px';
  ind.style.opacity = '1';
  ind.style.transform = 'translateY(' + (ar.top - nr.top + (ar.height - h) / 2) + 'px)';
}
window.addEventListener('resize', moveNavIndicator);
// 首次加载时把指示条摆到位（字体加载完尺寸才准，所以两帧后再量一次）
// ===== 健康条 =====
// 默认只显示一句话，把 gateway / hosts / 权限 / 希沃进程这些技术细节收进展开区。
// 有任何一项异常时自动展开一次，让问题显眼。
var healthAutoExpanded = false;
function syncHealthBar() {
  var summary = document.getElementById('healthSummary');
  var dot = document.getElementById('healthDot');
  var text = document.getElementById('healthText');
  var bar = document.getElementById('selfcheckBar');
  if (!summary || !dot || !text) return;
  var raw = (summary.textContent || '').trim();
  var bad = /异常|失败|未运行|未提权|不可用/.test(raw);
  dot.className = 'health-dot' + (bad ? ' bad' : '');
  text.textContent = raw || (bad ? '有项目需要处理' : '一切正常');
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

function bootNavIndicator() {
  bindAddPage();
  bindNavHover();
  bindHealthBar();
  moveNavIndicator();
  requestAnimationFrame(function () { requestAnimationFrame(moveNavIndicator); });
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bootNavIndicator);
else bootNavIndicator();

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
  bindClick('refreshQrBtn', function () { setQrStage('loading'); });
  watchQrStatus();
}
function confirmQrLogin() {
  // 直接展开扫码面板并开始，不再弹确认框（说明已常驻面板下方）
  if (typeof pickSide === "function") pickSide("qr");
  send({ type: "start-qr" });
}
function closeQrWarn() {
  var ov = document.getElementById('qrWarnOverlay');
  if (ov) ov.hidden = true;
}
// 次选：维持现状，继续用扫码
bindClick('qrWarnStayBtn', function () {
  closeQrWarn();
  send({ type: 'start-qr' });
});
// 优先：改用密码方式
bindClick('qrWarnPwdBtn', function () {
  closeQrWarn();
  navigate('add');
  showToast('在「账号密码」页填写手机号与密码即可；这种方式不受关机时间影响', 'info');
});
(function bindQrWarnMask() {
  var ov = document.getElementById('qrWarnOverlay');
  if (!ov) return;
  ov.addEventListener('click', function (e) { if (e.target === ov) closeQrWarn(); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !ov.hidden) closeQrWarn();
  });
})();

// ===== 切换账号口令 =====
function setSwitchPin() {
  var input = document.getElementById('newSwitchPinInput');
  var pin = input ? input.value.trim() : '';
  if (pin.length < 4) { showToast('口令至少 4 位', 'error'); return; }
  send({ type: 'set-switch-pin', pin: pin });
  if (input) input.value = '';
}
function clearSwitchPin() {
  send({ type: 'clear-switch-pin' });
}
// 程序化回填开关状态时，不能触发 change —— 否则启动时把 enabled=false 写进复选框，
// 会被下面的监听当成「用户取消勾选」而把口令清掉。
var switchPinSuppressChange = false;
(function bindSwitchPinToggle() {
  var box = document.getElementById('useSwitchPinCheck');
  if (!box) return;
  box.addEventListener('change', function () {
    if (switchPinSuppressChange) return;
    var panel = document.getElementById('switchPinPanel');
    if (panel) panel.style.display = this.checked ? 'block' : 'none';
    // 用户主动取消勾选 = 清除口令
    if (!this.checked) send({ type: 'clear-switch-pin' });
  });
})();

// ===== 受信任外链：作者主页 / 项目仓库 =====
// 前端只负责发起，实际打开由 C# 侧按域名白名单再校验一次后交给系统浏览器
function openExternalUrl(url) {
  send({ type: 'open-external', url: url });
}
bindClick('authorLink', function () { openExternalUrl('https://space.bilibili.com/1849305981'); });
bindClick('madeByLink', function () { openExternalUrl('https://space.bilibili.com/1849305981'); });
bindClick('openRepoBtn', function () { openExternalUrl('https://github.com/Pro-Qin/SeewoAutoLogin'); });
bindClick('openSiteBtn', function () { openExternalUrl('https://pro-qin.github.io/SeewoAutoLogin/'); });

bindClick('termsLink', openTerms);
bindClick('termsCloseBtn', closeTerms);
(function bindTermsOverlay() {
  const ov = document.getElementById('termsOverlay');
  if (!ov) return;
  // 点遮罩空白处关闭；点面板内部不关闭
  ov.addEventListener('click', function (e) { if (e.target === ov) closeTerms(); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !ov.hidden) closeTerms();
  });
})();

bindClick('factoryResetBtn', function () {
  if (!confirm('恢复出厂设置会清空所有账号、扫码凭据与全部设置，且不可撤销。\n\n确定继续吗？')) return;
  const el = document.getElementById('factoryResetStatus');
  if (el) el.textContent = '正在恢复，完成后会自动重启程序…';
  send({ type: 'factory-reset' });
  // 兜底：万一后台没响应，给出可操作的提示，而不是一直停在“正在恢复”
  setTimeout(function () {
    if (el && el.textContent.indexOf('正在恢复') === 0) {
      el.textContent = '恢复超时：请在托盘图标上右键 → 退出程序，然后重新打开。';
    }
  }, 20000);
});

bindClick('repairBtn', function() { repairSso(); });
bindClick('actHealthCheck', function() { runHealthCheck(); });
bindClick('batchImportBtn', function() { batchImport(); });
bindClick('importCsvBtn', function() { importCsvFile(); });
// 标签筛选的「清除」：下拉框右侧的 × 和提示条里的按钮都归零到「全部」
bindClick('tagFilterClear', function() { setTagFilter(''); });
bindClick('listBackupsBtn', function() { listBackups(); });

