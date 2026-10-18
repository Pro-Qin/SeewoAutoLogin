// update.js — 版本号、检查更新、下载进度与自动安装开关。依赖：core.js、ui.js。

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
  const received = msg.received || 0;
  const total = msg.total || -1;
  // 前台下载与后台静默下载走同一条渲染：只看 received/total，不关心是谁在拉
  if (state === 'downloading') {
    const prefix = msg.background ? '后台下载中 ' : '正在下载 ';
    text = total > 0
      ? prefix + Math.round(received * 100 / total) + '%（' + fmtMB(received) + ' / ' + fmtMB(total) + '）'
      : prefix + fmtMB(received);
  }
  const wrap = document.getElementById('updateProgressWrap');
  const bar = document.getElementById('updateProgressBar');
  if (wrap && bar) {
    // 进度条只在有总长度时才有意义，否则只会一直停在 0% 让人以为卡住
    const showBar = (state === 'downloading' && total > 0);
    wrap.style.display = showBar ? '' : 'none';
    bar.style.width = showBar ? Math.min(100, received * 100 / total).toFixed(1) + '%' : '0%';
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
  // 后台在下载时不禁用按钮：用户点下去才有机会按速率决定「接手显示」还是「掐断重下」，
  // 直接禁用等于把这条路堵死（那正是「点了没反应」的来源）。
  const foregroundDownload = busy && !msg.background;
  const btn = document.getElementById('downloadUpdateBtn');
  if (btn) {
    btn.disabled = foregroundDownload || installing;
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
