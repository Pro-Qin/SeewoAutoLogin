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
function openUpdatePage() { send({type:'open-update-page', url: window.__updateUrl || ''}); }
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
  const gwLevel = triLevel(st.gatewayRunning);
  let gwSub = '';
  if (gwLevel !== 'unknown') {
    gwSub = port ? (':' + port) : '';
    if (!st.gatewayRunning) gwSub = (gwSub ? gwSub + ' · ' : '') + '未运行';
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
    bar.title = '开机自启：' + auto + (st.autoStartMode ? '（' + st.autoStartMode + '）' : '');
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
bindClick('repairBtn', function() { repairSso(); });
bindClick('actHealthCheck', function() { runHealthCheck(); });
bindClick('batchImportBtn', function() { batchImport(); });
bindClick('listBackupsBtn', function() { listBackups(); });

