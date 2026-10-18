// backup.js — 配置备份列表与还原。依赖：core.js、ui.js。

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
