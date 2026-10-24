// core.js — C#↔JS 通信与全局骨架（send / state / applyConfig / bindClick / handleCSharpMessage 分发）。依赖：无，必须第一个加载。

// ===== C# ↔ JS Communication =====
function send(msg) {
  try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch(e) {}
}
try {
  window.chrome.webview.addEventListener('message', function(e) {
    handleCSharpMessage(e.data);
  });
} catch (e) { /* 非 WebView2 环境（例如浏览器预览）忽略 */ }

// ===== 文案（i18n）=====
// 语言包由 C# 在 init 消息里整份下发（Resources/i18n/<locale>.json）。
// 页面静态文案用 data-i18n="key"，属性用 data-i18n-title / data-i18n-placeholder / data-i18n-aria-label；
// 动态文案用 t('key')。键缺失时 t() 返回 fallback 或键名，不会把界面变成空白。
let I18N = {};
let LOCALE = 'zh-CN';

function t(key, fallback) {
  const value = I18N[key];
  if (value === undefined || value === null || value === '') {
    return fallback === undefined ? key : fallback;
  }
  return value;
}

function applyI18n() {
  document.querySelectorAll('[data-i18n]').forEach(function (el) {
    const value = I18N[el.getAttribute('data-i18n')];
    if (value) el.textContent = value;
  });
  [['title', 'data-i18n-title'],
   ['placeholder', 'data-i18n-placeholder'],
   ['aria-label', 'data-i18n-aria-label']].forEach(function (pair) {
    document.querySelectorAll('[' + pair[1] + ']').forEach(function (el) {
      const value = I18N[el.getAttribute(pair[1])];
      if (value) el.setAttribute(pair[0], value);
    });
  });
}

// ===== App State =====
let state = { currentPage:'accounts', accounts:[], selectedId:null, config:{}, qrActive:false, needsPassword:false, particles:true,
  performanceMode:false,
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
  applyPerformanceMode(!!cfg.performanceMode);
}
function updateLimitHint() {
  const hint = document.getElementById('activeLimitHint');
  if (hint) hint.textContent = '展示给希沃 · 最多' + maxVisible() + '个';
}

// ===== Handle Messages from C# =====
function handleCSharpMessage(raw) {
  let msg = raw;
  if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch (e) { return; } }
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'init':
      // 先落地语言包：后面的 render* 都按当前语言输出
      if (msg.i18n) { I18N = msg.i18n || {}; LOCALE = msg.locale || 'zh-CN'; applyI18n(); }
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
    case 'qr-countdown': {
      const cd = document.getElementById('qrCountdown');
      if (cd) {
        cd.textContent = msg.text;
        // 最后 10 秒标红：扫码的人需要知道二维码快过期了
        const nums = /(\d+)/.exec(msg.text || '');
        const sec = nums ? parseInt(nums[1], 10) : NaN;
        cd.classList.toggle('urgent', isFinite(sec) && sec <= 10);
      }
      break;
    }
    case 'settings':
      if (msg.passwordEnabled !== undefined) { document.getElementById('usePasswordCheck').checked = msg.passwordEnabled; document.getElementById('passwordPanel').style.display = msg.passwordEnabled ? 'block' : 'none'; }
      if (msg.passwordSet !== undefined) document.getElementById('passwordStatus').textContent = msg.passwordSet ? '密码已设置' : '密码未设置';
      if (msg.rotationEnabled!==undefined) document.getElementById('rotationEnabledCheck').checked = msg.rotationEnabled;
      if (msg.rotationGroupSize!==undefined) document.getElementById('rotationGroupSize').value = msg.rotationGroupSize;
      if (msg.autoStart!==undefined) document.getElementById('autoStartCheck').checked = msg.autoStart;
      if (msg.minimizeToTray!==undefined) document.getElementById('minimizeToTrayCheck').checked = msg.minimizeToTray;
      if (msg.startMinimized!==undefined) document.getElementById('startMinimizedCheck').checked = msg.startMinimized;
  // 粒子背景按保存的开关初始化：initParticles 是无条件启动的，关过的人必须在这被按住
  if (msg.particleBackground !== undefined) {
    var pToggle = document.getElementById('particleToggle');
    if (pToggle) pToggle.checked = !!msg.particleBackground;
    if (msg.particleBackground === false) stopParticles();
  }
      if (msg.restoreHostsOnExit!==undefined) document.getElementById('restoreHostsOnExitCheck').checked = msg.restoreHostsOnExit;
      if (msg.autoRepairEnabled!==undefined) document.getElementById('autoRepairCheck').checked = msg.autoRepairEnabled;
      if (msg.autoShowOverlay!==undefined) document.getElementById('autoShowOverlayCheck').checked = msg.autoShowOverlay;
      if (msg.performanceMode!==undefined) {
        const perfBox = document.getElementById('perfModeCheck');
        if (perfBox) perfBox.checked = !!msg.performanceMode;
        applyPerformanceMode(!!msg.performanceMode);
      }
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
    case 'upgrade-notice': showUpgradeNotice(msg); break;
    case 'version': setVersion(msg.current); break;
    case 'status': renderSelfCheck(msg.status || msg); break;
    case 'health': renderHealth(msg); break;
    case 'batch-import-result': renderBatchImportResult(msg); break;
    case 'csv-imported': renderCsvImportResult(msg); break;
    case 'backups': renderBackups(msg); break;
    case 'login-history': renderLoginHistory(msg); break;
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
    case 'logs-exported': {
      const dstLogs = document.getElementById('diagnosticsStatus');
      if (dstLogs) dstLogs.textContent = msg.ok ? ('已放到桌面：' + (msg.path || '')) : (msg.message || '导出失败');
      showToast(msg.ok ? '日志已导出到桌面' : (msg.message || '日志导出失败'), msg.ok ? 'ok' : 'error');
      break;
    }
    case 'seewo-version-changed':
      showToast('检测到希沃客户端已更新，如遇登录异常请反馈', 'warn');
      break;
  }
}

// ===== 静态控件绑定（不使用内联 onclick 拼接）=====
function bindClick(id, fn) {
  const el = document.getElementById(id);
  if (el) el.addEventListener('click', fn);
}
