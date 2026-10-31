// package.js — 导入配置包：差异预览与勾选。依赖：core.js、ui.js
// 后端只负责解密与比对，界面只负责"让用户看清要改什么"。

function renderPackagePreview(msg) {
  const overlay = document.getElementById('packageModal');
  if (!overlay) return;

  const accounts = msg.accounts || [];
  const settings = msg.settings || [];

  const meta = document.getElementById('packageMeta');
  if (meta) {
    const from = msg.packageVersion ? ('来自 v' + msg.packageVersion) : '来自未知版本';
    meta.textContent = from + ' · 导出于 ' + (msg.createdAt || '未知时间') +
      ' · 共 ' + accounts.length + ' 个账号，其中 ' + (msg.changedAccounts || 0) + ' 个与本机不同';
  }

  const accountBox = document.getElementById('packageAccounts');
  if (accountBox) {
    if (!accounts.length) {
      accountBox.innerHTML = '<div class="package-empty">这个配置包里没有账号</div>';
    } else {
      accountBox.innerHTML = accounts.map(a => {
        const changed = a.kind !== 'unchanged';
        const cls = a.kind === 'added' ? 'badge-new' : (a.kind === 'updated' ? 'badge-upd' : 'badge-same');
        return '<label class="package-item' + (changed ? '' : ' muted') + '">'
          + '<input type="checkbox" data-username="' + escAttr(a.username) + '"' + (changed ? ' checked' : ' disabled') + '>'
          + '<span class="pi-main">' + esc(a.displayName || a.username) + '</span>'
          + '<span class="pi-tag ' + cls + '">' + esc(a.summary) + '</span>'
          + '</label>';
      }).join('');
    }
  }

  const settingBox = document.getElementById('packageSettings');
  if (settingBox) {
    if (!settings.length) {
      settingBox.innerHTML = '<div class="package-empty">设置与本机完全一致，没有需要应用的项</div>';
    } else {
      settingBox.innerHTML = settings.map(s =>
        '<label class="package-item">'
        + '<input type="checkbox" data-setting="' + escAttr(s.key) + '" checked>'
        + '<span class="pi-main">' + esc(s.label) + '</span>'
        + '<span class="pi-side">' + esc(s.current) + ' → ' + esc(s.incoming) + '</span>'
        + '</label>'
      ).join('');
    }
  }

  overlay.hidden = false;
}

function closePackage() {
  const overlay = document.getElementById('packageModal');
  if (overlay) overlay.hidden = true;
  send({ type: 'cancel-import' });
}

function applyPackage() {
  const overlay = document.getElementById('packageModal');
  const accounts = Array.from(document.querySelectorAll('#packageAccounts input[data-username]:checked'))
    .map(el => el.getAttribute('data-username'));
  const settings = Array.from(document.querySelectorAll('#packageSettings input[data-setting]:checked'))
    .map(el => el.getAttribute('data-setting'));

  if (!accounts.length && !settings.length) {
    showToast('没有勾选任何项目', 'warn');
    return;
  }

  send({ type: 'apply-import', accounts: accounts, settings: settings });
  if (overlay) overlay.hidden = true;
}

(function bindPackageModal() {
  const close = document.getElementById('packageClose');
  if (close) close.addEventListener('click', closePackage);

  const apply = document.getElementById('packageApply');
  if (apply) apply.addEventListener('click', applyPackage);

  const allAccounts = document.getElementById('packageAccountAll');
  if (allAccounts) allAccounts.addEventListener('click', function () {
    const boxes = Array.from(document.querySelectorAll('#packageAccounts input[data-username]:not(:disabled)'));
    const allChecked = boxes.every(b => b.checked);
    boxes.forEach(b => { b.checked = !allChecked; });
  });

  const allSettings = document.getElementById('packageSettingAll');
  if (allSettings) allSettings.addEventListener('click', function () {
    const boxes = Array.from(document.querySelectorAll('#packageSettings input[data-setting]'));
    const allChecked = boxes.every(b => b.checked);
    boxes.forEach(b => { b.checked = !allChecked; });
  });
})();
