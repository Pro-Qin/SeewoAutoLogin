// upgrade.js — 升级完成提醒、更新内容浮窗、桌面提醒预览。依赖：core.js、ui.js。

// ===== 升级完成提醒（后台自动更新后第一次打开主界面时提示一次）=====
// 动效分工：外层容器负责滑入/滑出，内层卡片负责停留期间的轻微浮动。
// 两者若作用在同一个元素的 transform 上会互相覆盖，分层才能各自稳住。
const UPGRADE_NOTICE_AUTO_MS = 10000;
let __upgradeChangelogText = '';
let __upgradeTimerHandle = null;

function upgradeFmtVersion(v) {
  const t = String(v == null ? '' : v).replace(/^\s*v/i, '').trim();
  return t ? ('v' + t) : '';
}

function showUpgradeNotice(msg) {
  msg = msg || {};
  const box = document.getElementById('upgradeNotice');
  if (!box) return;

  const fromEl = document.getElementById('upgradeFrom');
  const toEl = document.getElementById('upgradeTo');
  const timeEl = document.getElementById('upgradeTime');
  const titleEl = document.getElementById('upgradeTitle');
  const fromText = upgradeFmtVersion(msg.from);
  const toText = upgradeFmtVersion(msg.to);

  // 没有起止对比数据时不编造：只显示当前版本
  if (fromEl) {
    fromEl.textContent = fromText || '上一版本';
    fromEl.style.display = fromText ? '' : 'none';
  }
  const arrow = document.querySelector('.uv-arrow');
  if (arrow) arrow.style.display = fromText ? '' : 'none';
  if (toEl) toEl.textContent = toText;
  if (titleEl) titleEl.textContent = msg.debug ? '升级提醒 · 预览' : '升级完成';
  if (timeEl) {
    timeEl.textContent = msg.atText ? (msg.atText + ' 更新') : '';
    timeEl.style.display = msg.atText ? '' : 'none';
  }

  __upgradeChangelogText = msg.notes || '';

  // 每次展示都完整重放一次滑入：先复位（关掉过渡）→ 强制回流 → 再挂 show
  box.hidden = false;
  box.classList.remove('show');
  box.style.transition = 'none';
  void box.offsetWidth;
  box.style.transition = '';
  box.classList.add('show');

  restartUpgradeTimer();
}

function restartUpgradeTimer() {
  const bar = document.getElementById('upgradeTimerBar');
  if (__upgradeTimerHandle) { clearTimeout(__upgradeTimerHandle); __upgradeTimerHandle = null; }
  if (bar) {
    bar.style.transition = 'none';
    bar.style.transform = 'scaleX(1)';
    void bar.offsetWidth;
    bar.style.transition = 'transform ' + (UPGRADE_NOTICE_AUTO_MS / 1000) + 's linear';
    bar.style.transform = 'scaleX(0)';
  }
  // 10 秒后自动滑出（按需求固定 10 秒，悬停不暂停）
  __upgradeTimerHandle = setTimeout(function () { hideUpgradeNotice(); }, UPGRADE_NOTICE_AUTO_MS);
}

function hideUpgradeNotice() {
  const box = document.getElementById('upgradeNotice');
  if (__upgradeTimerHandle) { clearTimeout(__upgradeTimerHandle); __upgradeTimerHandle = null; }
  if (!box || box.hidden) return;
  box.classList.remove('show');   // 触发滑出过渡
  setTimeout(function () {
    // 过渡结束后真正隐藏；期间若又触发了一次展示，show 会被重新加上，这里就不要再收起
    if (!box.classList.contains('show')) box.hidden = true;
  }, 700);
}

// 更新内容：主界面内浮窗展示，不走系统浏览器
function showChangelog(text) {
  const ov = document.getElementById('changelogOverlay');
  if (!ov) return;
  renderChangelog(text);
  ov.hidden = false;
}

function renderChangelog(text) {
  const body = document.getElementById('changelogBody');
  if (!body) return;
  body.innerHTML = '';
  const frag = document.createDocumentFragment();
  const lines = String(text || '').split('\n');
  let empty = true;
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].replace(/\s+$/, '').trim();
    if (!t) continue;
    empty = false;
    const div = document.createElement('div');
    if (/^[-*·]\s+/.test(t) || /^\d+[.)]\s+/.test(t)) {
      div.className = 'cl-item';
      div.textContent = t.replace(/^[-*·]\s+/, '').replace(/^\d+[.)]\s+/, '');
    } else if (/^#{1,6}\s+/.test(t)) {
      div.className = 'cl-head';
      div.textContent = t.replace(/^#{1,6}\s+/, '');
    } else {
      div.className = 'cl-p';
      div.textContent = t;
    }
    frag.appendChild(div);
  }
  if (empty) {
    const none = document.createElement('div');
    none.className = 'cl-empty';
    none.textContent = '这一版没有记录更新内容。';
    frag.appendChild(none);
  }
  body.appendChild(frag);
  body.scrollTop = 0;
}

function closeChangelog() {
  const ov = document.getElementById('changelogOverlay');
  if (ov) ov.hidden = true;
}

function debugUpgradeNotice() { send({ type: 'debug-upgrade-notice' }); }

bindClick('upgradeNoticeOk', hideUpgradeNotice);
bindClick('upgradeNoticeClose', hideUpgradeNotice);
bindClick('upgradeNoticeChangelog', function () {
  // 先收起提醒再开浮窗，避免右下角残留一张卡片压着内容
  hideUpgradeNotice();
  showChangelog(__upgradeChangelogText);
});
bindClick('changelogCloseBtn', closeChangelog);
(function bindChangelogOverlay() {
  const ov = document.getElementById('changelogOverlay');
  if (!ov) return;
  ov.addEventListener('click', function (e) { if (e.target === ov) closeChangelog(); });
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    if (!ov.hidden) { closeChangelog(); return; }
    hideUpgradeNotice();
  });
})();

// 桌面级提醒预览：由 C# 弹一个独立窗口，与主界面无关
function debugDesktopNotice() { send({ type: 'debug-desktop-notice' }); }
