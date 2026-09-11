// ===== C# ↔ JS Communication =====
function send(msg) {
  try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch(e) {}
}
window.chrome.webview.addEventListener('message', function(e) {
  handleCSharpMessage(e.data);
});

// ===== App State =====
let state = { currentPage:'accounts', accounts:[], selectedId:null, config:{}, qrActive:false, needsPassword:false, particles:true };

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
function handleCSharpMessage(msg) {
  switch (msg.type) {
    case 'init':
      state.accounts = msg.accounts || []; renderAccounts(); updateStatusBar();
      if (msg.version) setVersion(msg.version);
      if (msg.needsPassword) showLock();
      break;
    case 'account-list':
      state.accounts = msg.accounts || []; state.selectedId = null; renderAccounts(); updateStatusBar();
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
  const active = Math.min(total, 6);
  const el = document.getElementById('accountCountText');
  if (el) el.textContent = total > 0 ? `${active}/${total}` : '';
}

// ===== Two-Column Render =====
function renderAccounts() {
  const accounts = state.accounts || [];
  const empty = document.getElementById('emptyState');
  const activeCol = document.getElementById('activeList');
  const inactiveCol = document.getElementById('inactiveList');
  const full = accounts.length >= 6;

  if (accounts.length === 0) {
    activeCol.innerHTML = ''; inactiveCol.innerHTML = '';
    empty.style.display = 'block'; return;
  }
  empty.style.display = 'none';

  const active = accounts.slice(0, 6);
  const inactive = accounts.slice(6);

  document.getElementById('activeCount').textContent = active.length;
  document.getElementById('inactiveCount').textContent = inactive.length;

  activeCol.innerHTML = active.map((a,i) => cardHtml(a, true, i, full)).join('');
  inactiveCol.innerHTML = inactive.map((a,i) => cardHtml(a, false, i, full)).join('');

  updateActionBar();
}

function cardHtml(a, isActive, idx, isFull) {
  const sel = state.selectedId === a.id ? ' selected' : '';
  const freq = a.requestCount > 0 ? '<span class="freq" title="最近SSO请求: ' + esc(a.lastRequestAtUtc || '-') + '">' + a.requestCount + '次</span>' : '';
  const switchBtn = isActive
    ? `<button class="switch-btn" onclick="event.stopPropagation();switchToInactive('${a.id}')" title="移到未生效">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 4l-1.41 1.41L16.17 11H4v2h12.17l-5.58 5.59L12 20l8-8z"/></svg>
      </button>`
    : (isFull
        ? `<button class="switch-btn disabled" disabled title="正在生效账号位已满">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 4l1.41 1.41L7.83 11H20v2H7.83l5.58 5.59L12 20l-8-8z"/></svg>
          </button>`
        : `<button class="switch-btn" onclick="event.stopPropagation();switchToActive('${a.id}')" title="移到生效">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M12 4l1.41 1.41L7.83 11H20v2H7.83l5.58 5.59L12 20l-8-8z"/></svg>
          </button>`);
  return `<div class="acct-card${sel}" data-id="${a.id}" onclick="selectAccount('${a.id}')">
    <div class="row1">
      <div class="avatar-mini"><span>${esc(a.initial||'S')}</span></div>
      <div class="info">
        <div class="name">${esc(a.displayName||a.username||'')}</div>
        <div class="meta">${esc(a.username||'')} · ${esc(a.loginType||'')}</div>
      </div>
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
