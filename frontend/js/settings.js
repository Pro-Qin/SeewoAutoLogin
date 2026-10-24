// settings.js — 设置页开关监听、插件密码、锁定遮罩、切换账号口令。依赖：core.js、ui.js、qr.js、update.js、particles.js。

// ===== Settings =====
document.addEventListener('change', function(e) {
  switch(e.target.id) {
    case 'usePasswordCheck': send({type:'update-setting', key:'usePluginPassword', value:e.target.checked}); const pp = document.getElementById('passwordPanel'); if(pp) pp.style.display = e.target.checked?'block':'none'; break;
    case 'rotationEnabledCheck': send({type:'update-setting', key:'userListRotationEnabled', value:e.target.checked}); break;
    case 'autoStartCheck': send({type:'update-setting', key:'autoStart', value:e.target.checked}); break;
    case 'minimizeToTrayCheck': send({type:'update-setting', key:'minimizeToTray', value:e.target.checked}); break;
    case 'startMinimizedCheck': send({type:'update-setting', key:'startMinimized', value:e.target.checked}); break;
    case 'restoreHostsOnExitCheck': send({type:'update-setting', key:'restoreHostsOnExit', value:e.target.checked}); break;
    case 'autoRepairCheck': send({type:'update-setting', key:'autoRepairEnabled', value:e.target.checked}); break;
    case 'autoShowOverlayCheck': send({type:'update-setting', key:'autoShowOverlay', value:e.target.checked}); break;
    case 'autoCheckUpdateCheck': send({type:'update-setting', key:'autoCheckUpdate', value:e.target.checked}); break;
    case 'autoInstallUpdateCheck': onAutoInstallSwitchChanged(e.target.checked); break;
    case 'autoInstallUpdateSettingsCheck': onAutoInstallSwitchChanged(e.target.checked); break;
    case 'rotationGroupSize': send({type:'update-setting', key:'userListRotationGroupSize', value:parseInt(e.target.value)}); break;
    case 'tagFilter': setTagFilter(e.target.value); break;
    case 'particleToggle': toggleParticles(e.target.checked); break;
    case 'perfModeCheck': setPerformanceMode(e.target.checked); break;
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
// ===== 设置页：左侧类目 + 搜索 =====
// 7 个分组、30 多个开关平铺一屏太吵：左侧只留类目，右侧一次只显示一组。
// 搜索框输入时临时忽略类目，直接列出命中的卡片（找具体开关最快）。
function showSettingsGroup(group) {
  const pane = document.getElementById('settingsPane');
  if (!pane) return;
  state.settingsGroup = group;
  const search = document.getElementById('settingsSearch');
  if (search) search.value = '';
  pane.querySelectorAll('.card[data-group]').forEach(function (card) {
    card.classList.toggle('shown', card.getAttribute('data-group') === group);
  });
  document.querySelectorAll('.settings-nav-item').forEach(function (item) {
    item.classList.toggle('active', item.getAttribute('data-group') === group);
  });
  const scroller = document.getElementById('content');
  if (scroller) scroller.scrollTop = 0;
}

function filterSettings(keyword) {
  const pane = document.getElementById('settingsPane');
  if (!pane) return;
  const q = String(keyword || '').trim().toLowerCase();
  if (!q) { showSettingsGroup(state.settingsGroup || 'general'); return; }
  pane.querySelectorAll('.card[data-group]').forEach(function (card) {
    card.classList.toggle('shown', card.textContent.toLowerCase().indexOf(q) >= 0);
  });
  document.querySelectorAll('.settings-nav-item').forEach(function (item) {
    item.classList.remove('active');
  });
}

function bindSettingsLayout() {
  document.querySelectorAll('.settings-nav-item').forEach(function (item) {
    item.addEventListener('click', function () { showSettingsGroup(this.getAttribute('data-group')); });
  });
  const search = document.getElementById('settingsSearch');
  if (search) search.addEventListener('input', function () { filterSettings(this.value); });
  showSettingsGroup(state.settingsGroup || 'general');
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
