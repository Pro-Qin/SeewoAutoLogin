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
  maxVisible:6, status:null, health:{}, backupsLoading:false };

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
  ['updateStatus','settingsUpdateStatus'].forEach(function (id) {
    const el = document.getElementById(id);
    if (el) { el.textContent = text; el.className = 'update-status' + (state === 'error' ? ' error' : ''); }
  });
  const busy = (state === 'preparing' || state === 'downloading');
  const btn = document.getElementById('downloadUpdateBtn');
  if (btn) btn.disabled = busy;
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
function renderAccounts() {
  const accounts = state.accounts || [];
  const empty = document.getElementById('emptyState');
  const activeCol = document.getElementById('activeList');
  const inactiveCol = document.getElementById('inactiveList');
  const limit = maxVisible();
  const full = accounts.length >= limit;
  updateLimitHint();

  if (accounts.length === 0) {
    activeCol.innerHTML = ''; inactiveCol.innerHTML = '';
    empty.style.display = 'block'; return;
  }
  empty.style.display = 'none';

  const active = accounts.slice(0, limit);
  const inactive = accounts.slice(limit);

  document.getElementById('activeCount').textContent = active.length;
  document.getElementById('inactiveCount').textContent = inactive.length;

  activeCol.innerHTML = active.map((a,i) => cardHtml(a, true, i, full)).join('');
  inactiveCol.innerHTML = inactive.map((a,i) => cardHtml(a, false, i, full)).join('');

  updateActionBar();
}

function cardHtml(a, isActive, idx, isFull) {
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
  return `<div class="acct-card${sel}" data-id="${escAttr(a.id)}">
    <div class="row1">
      <div class="avatar-mini"><span>${esc(a.initial||'S')}</span></div>
      <div class="info">
        <div class="name">${esc(a.displayName||a.username||'')}</div>
        <div class="meta">${esc(a.username||'')} · ${esc(a.loginType||'')}</div>
      </div>
      ${healthDot}
      ${freq}
      ${switchBtn}
    </div>
  </div>`;
}

function switchToActive(id) {
  send({type:'move-to-active', id});
}
function switchToInactive(id) {
  send({type:'move-to-inactive', id});
}

function selectAccount(id) {
  state.selectedId = state.selectedId === id ? null : id;
  renderAccounts();
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
  const name = prompt('输入新的显示名称：', acct ? (acct.displayName || acct.username || '') : '');
  if (name && name.trim()) send({type:'edit-display-name', id:state.selectedId, name:name.trim()});
}
function actEditTags() {
  if (!state.selectedId) return;
  const acct = state.accounts.find(a => a.id === state.selectedId);
  const cur = acct && acct.tags ? acct.tags : '';
  const tags = prompt('输入标签（逗号分隔）：', cur);
  if (tags !== null) send({type:'edit-tags', id:state.selectedId, tags});
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
    case 'rotationGroupSize': send({type:'update-setting', key:'userListRotationGroupSize', value:parseInt(e.target.value)}); break;
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
function addFakeAccount() { const n = prompt('输入假账号名称：','测试用户'+Math.floor(Math.random()*100)); if(n&&n.trim()) send({type:'add-fake-account',displayName:n.trim()}); }

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
bindClick('listBackupsBtn', function() { listBackups(); });

