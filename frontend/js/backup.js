// backup.js — 配置备份：独立弹窗里列出、恢复、删除。依赖：core.js、ui.js。

let backupsTimer = null;

function openBackupModal() {
  const modal = document.getElementById('backupModal');
  if (modal) modal.hidden = false;
}
function closeBackupModal() {
  const modal = document.getElementById('backupModal');
  if (modal) modal.hidden = true;
}

function listBackups() {
  // 以前列表直接内联展开在设置页里：十几条一铺，设置页要滚很久才能看到下面的分组。
  // 改成弹窗之后，设置页保持稳定高度，备份再多也不影响布局。
  openBackupModal();
  const list = document.getElementById('backupList');
  if (list) list.innerHTML = '<div class="backup-empty">' + esc(t('backup.loading', '正在加载备份列表…')) + '</div>';
  send({ type: 'list-backups' });
  state.backupsLoading = true;
  if (backupsTimer) clearTimeout(backupsTimer);
  backupsTimer = setTimeout(function () {
    backupsTimer = null;
    if (!state.backupsLoading) return;
    state.backupsLoading = false;
    const l = document.getElementById('backupList');
    if (l) l.innerHTML = '<div class="backup-empty">' + esc(t('backup.timeout', '加载超时，请重试')) + '</div>';
  }, 15000);
}

function backupReasonText(reason) {
  const map = {
    account: t('backup.reason.account', '账号变动'),
    settings: t('backup.reason.settings', '设置变更'),
    manual: t('backup.reason.manual', '手动备份'),
    import: t('backup.reason.import', '导入配置'),
    update: t('backup.reason.update', '版本更新')
  };
  return map[reason] || '';
}

function formatBackupSize(bytes) {
  const n = parseInt(bytes, 10) || 0;
  if (n <= 0) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
  return (n / 1024 / 1024).toFixed(1) + ' MB';
}

function renderBackups(msg) {
  msg = msg || {};
  if (backupsTimer) { clearTimeout(backupsTimer); backupsTimer = null; }
  state.backupsLoading = false;
  const list = document.getElementById('backupList');
  if (!list) return;

  const items = msg.items || [];
  if (!items.length) {
    list.innerHTML = '<div class="backup-empty">' + esc(t('backup.empty', '暂无备份')) + '</div>';
    return;
  }

  list.innerHTML = items.map(function (it) {
    it = it || {};
    const meta = [];
    if (it.accounts !== undefined && it.accounts !== null) meta.push((parseInt(it.accounts, 10) || 0) + ' ' + t('backup.accountsUnit', '个账号'));
    const size = formatBackupSize(it.size);
    if (size) meta.push(size);
    const reason = backupReasonText(it.reason);
    return '<div class="backup-item">'
      + '<div class="backup-info">'
      +   '<div class="backup-time">' + esc(it.time || '') + (reason ? '<span class="backup-tag">' + esc(reason) + '</span>' : '') + '</div>'
      +   '<div class="backup-file">' + esc(meta.join(' · ')) + '</div>'
      + '</div>'
      + '<div class="backup-actions">'
      +   '<button class="btn btn-secondary btn-small backup-restore" data-name="' + escAttr(it.name || '') + '">' + esc(t('backup.restore', '恢复')) + '</button>'
      +   '<button class="btn btn-danger btn-small backup-delete" data-name="' + escAttr(it.name || '') + '">' + esc(t('backup.delete', '删除')) + '</button>'
      + '</div>'
      + '</div>';
  }).join('');
}

function restoreBackup(name) {
  if (!name) return;
  if (!confirm(t('backup.restoreConfirm', '确定恢复这份备份吗？当前配置将被覆盖，此操作不可撤销。'))) return;
  send({ type: 'restore-backup', name: name });
  showToast(t('backup.restoring', '正在恢复备份…'), 'info');
}

function deleteBackup(name) {
  if (!name) return;
  if (!confirm(t('backup.deleteConfirm', '删除这份备份？删除后无法恢复。'))) return;
  send({ type: 'delete-backup', name: name });
}

document.addEventListener('click', function (e) {
  const target = (e.target && e.target.closest) ? e.target.closest('.backup-restore, .backup-delete') : null;
  if (!target) return;
  const name = target.getAttribute('data-name') || '';
  if (target.classList.contains('backup-restore')) restoreBackup(name);
  else deleteBackup(name);
});
