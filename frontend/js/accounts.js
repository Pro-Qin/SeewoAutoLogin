// accounts.js — 账号两栏列表、卡片渲染、标签筛选、选中与操作栏、拖拽排序。依赖：core.js、ui.js、nav.js。

// ===== Status Bar =====
function updateStatusBar() {
  const total = (state.accounts||[]).length;
  const active = Math.min(total, maxVisible());
  const el = document.getElementById('accountCountText');
  if (el) el.textContent = total > 0 ? `${active}/${total}` : '';
}

// ===== Two-Column Render =====
let accountListRendered = false;

function renderAccounts() {
  const accounts = state.accounts || [];
  const empty = document.getElementById('emptyState');
  const activeCol = document.getElementById('activeList');
  const inactiveCol = document.getElementById('inactiveList');
  const limit = maxVisible();
  const full = accounts.length >= limit;
  updateLimitHint();
  renderTagFilter();

  if (accounts.length === 0) {
    activeCol.innerHTML = ''; inactiveCol.innerHTML = '';
    setEmptyState(true, '暂无账号'); return;
  }

  // 生效区 / 未生效区的切分始终按后端下发的真实顺序算，
  // 标签筛选只决定「哪些卡片显示出来」，不会改变谁在生效区、谁排第几。
  const active = filterByTag(accounts.slice(0, limit));
  const inactive = filterByTag(accounts.slice(limit));

  if (active.length === 0 && inactive.length === 0) {
    activeCol.innerHTML = ''; inactiveCol.innerHTML = '';
    setEmptyState(true, '没有带「' + state.filterTag + '」标签的账号');
    return;
  }
  setEmptyState(false, '');

  document.getElementById('activeCount').textContent = active.length;
  document.getElementById('inactiveCount').textContent = inactive.length;

  // 入场动画只在列表首次出现时播放。
  // 之前每次 renderAccounts() 都会重建卡片 DOM，于是点选账号、切换生效区这类操作
  // 也会让整列卡片重新"飞入"一遍，看起来像在闪烁。
  const animate = !accountListRendered;
  accountListRendered = true;

  activeCol.innerHTML = active.map((a,i) => cardHtml(a, true, i, full, animate)).join('');
  inactiveCol.innerHTML = inactive.map((a,i) => cardHtml(a, false, i, full, animate)).join('');

  updateActionBar();
}

// ===== 标签筛选（只影响显示，后端数据与生效区顺序都不变）=====
function accountTags(a) {
  if (!a || !a.tags) return [];
  const raw = Array.isArray(a.tags) ? a.tags : String(a.tags).split(/[,，;；]/);
  return raw.map(function(t) { return String(t).trim(); }).filter(function(t) { return t.length > 0; });
}
function filterByTag(list) {
  const tag = state.filterTag;
  if (!tag) return list || [];
  return (list || []).filter(function(a) { return accountTags(a).indexOf(tag) >= 0; });
}
function collectTags() {
  const tags = [];
  (state.accounts || []).forEach(function(a) {
    accountTags(a).forEach(function(t) {
      if (!tags.some(function(x) { return x.toLowerCase() === t.toLowerCase(); })) tags.push(t);
    });
  });
  return tags;
}
function setEmptyState(show, text) {
  const empty = document.getElementById('emptyState');
  if (!empty) return;
  empty.style.display = show ? 'block' : 'none';
  if (show && text) {
    const t = empty.querySelector('.empty-text');
    if (t) t.textContent = text;
  }
}
function setTagFilter(tag) {
  state.filterTag = tag || '';
  renderAccounts();
}
// 下拉框选项只在标签集合变化时重建：否则用户每选一次都会让下拉框收起、丢掉焦点
function renderTagFilter() {
  const sel = document.getElementById('tagFilter');
  const tags = collectTags();
  // 正在筛选的标签已经不存在了（删账号 / 改标签）就自动回到「全部」，免得列表一直是空的
  if (state.filterTag && !tags.some(function(t) { return t === state.filterTag; })) state.filterTag = '';

  if (sel) {
    const signature = tags.join('\u0001');
    if (sel.getAttribute('data-tags') !== signature) {
      sel.innerHTML = '<option value="">全部标签</option>'
        + tags.map(function(t) { return '<option value="' + escAttr(t) + '">' + esc(t) + '</option>'; }).join('');
      sel.setAttribute('data-tags', signature);
    }
    const cur = state.filterTag || '';
    sel.value = cur;
    if (sel.value !== cur) sel.value = '';
    sel.disabled = tags.length === 0;
    sel.classList.toggle('active', !!cur);
  }

  const clearBtn = document.getElementById('tagFilterClear');
  if (clearBtn) clearBtn.style.display = state.filterTag ? '' : 'none';

  updateTagFilterHint();
}
function updateTagFilterHint() {
  const hint = document.getElementById('tagFilterHint');
  if (!hint) return;
  const tag = state.filterTag;
  if (!tag) { hint.style.display = 'none'; hint.innerHTML = ''; return; }
  const total = (state.accounts || []).length;
  const shown = filterByTag(state.accounts || []).length;
  hint.innerHTML = '<span>当前筛选：<b>' + esc(tag) + '</b> · 显示 ' + shown + ' / ' + total + ' 个账号</span>'
    + '<span style="flex:1"></span>'
    + '<button type="button" class="tag-filter-clear-btn" id="tagFilterClearBtn">清除筛选</button>';
  hint.style.display = 'flex';
  bindClick('tagFilterClearBtn', function() { setTagFilter(''); });
}

function cardHtml(a, isActive, idx, isFull, animate) {
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
  // 后台自动续期状态：让「这个账号有没有被自动刷新过」一眼可见，
  // 而不是只能靠账号突然不可用来发现问题。占位账号不参与续期，不显示。
  // 续期状态行：失败 / 网络异常时把原因写在这一行。
  // 以前只有「续期失败 · 3 分钟前」，原因塞在健康小圆点的 title 里，不悬停根本看不到；
  // 用户点完「续期」只得到一句「失败 N 个」，无处可查。
  let renewLine = '';
  const renewFailed = hState === 'bad';
  const renewTransient = hState === 'warn';
  if (!a.isPlaceholder && (a.lastTokenExchangeAtUtc || renewFailed || renewTransient)) {
    const when = a.lastTokenExchangeAtUtc || a.lastHealthCheckAtUtc;
    const captcha = /图片验证码/.test(hMsg || '');
    if (captcha) {
      // 被希沃要求图片验证码：这不是密码错，也不该让用户去改密码。
      // 卡片上说清"被什么拦住了"，问号里说清"多久后自动重试、还能怎么解"。
      const line = t('accounts.renewCaptcha', '续期失败 · {0} · 被希沃图片验证码拦截')
        .replace('{0}', when ? relativeTime(when) : '刚刚');
      const hint = t('accounts.renewCaptchaHint', '登录频率过高触发风控，约 2 小时后自动重试');
      renewLine = '<div class="renew-line bad">' + esc(line)
        + '<span class="help-dot" title="' + escAttr(hint) + '">?</span>'
        + '</div>';
    } else {
      const reason = (renewFailed || renewTransient) ? renewReason(hMsg) : '';
      renewLine = '<div class="renew-line' + (renewFailed ? ' bad' : '') + '" title="' + escAttr(hMsg || '') + '">'
        + (renewFailed ? '续期失败 · ' : (renewTransient ? '网络异常，自动重试 · ' : '已自动续期 · '))
        + esc(when ? relativeTime(when) : '刚刚')
        + (reason ? ' · ' + esc(reason) : '')
        + '</div>';
    }
  }

  // 扫码账号的凭据一旦过期就无法自动恢复（希沃只发一个 token，没有 refresh token），
  // 所以直接给一个补救入口：重新扫一次，登录后会自动替换该账号的凭据，不用删了重加。
  const rescanBtn = (!a.isPlaceholder && a.loginType === '扫码')
    ? `<button class="switch-btn rescan${hState === 'bad' ? ' urgent' : ''}" data-rescan="${escAttr(a.id)}"
         title="重新扫码：扫码登录后会自动替换该账号的凭据">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M17.65 6.35A7.96 7.96 0 0 0 12 4a8 8 0 1 0 7.73 10h-2.08A6 6 0 1 1 12 6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/></svg>
      </button>`
    : '';

  return `<div class="acct-card${sel}${animate ? ' enter' : ''}" draggable="true" data-id="${escAttr(a.id)}">
    <div class="row1">
      <div class="avatar-mini"><span>${esc(a.initial||'S')}</span></div>
      <div class="info">
        <div class="name">${esc(a.displayName||a.username||'')}</div>
        <div class="meta">${esc(a.username||'')} · ${esc(a.loginType||'')}</div>
        ${renewLine}
      </div>
      ${healthDot}
      ${freq}
      ${rescanBtn}
      ${switchBtn}
    </div>
  </div>`;
}

// 续期失败原因压缩成一行：去掉与状态词重复的前缀，过长截断。
// 服务端给的是完整说明（例如「网络异常，1 分钟后自动重试：请求超时」），
// 卡片宽度放不下，这里只保留最关键的那半句。
function renewReason(text) {
  var s = String(text || '').trim();
  if (!s) return '';
  s = s.replace(/^网络异常，[^：:]*[：:]/, '')
       .replace(/^凭据失效[：:]/, '')
       .replace(/（\d+\s*分钟后自动重试）$/, '');
  return s.length > 46 ? s.slice(0, 46) + '…' : s;
}

// 把时间戳转成人话（刚刚 / N 分钟前 / N 小时前 / N 天前）
function relativeTime(text) {
  const t = new Date(String(text).replace(' ', 'T'));
  if (isNaN(t.getTime())) return text;
  const sec = (Date.now() - t.getTime()) / 1000;
  if (sec < 60) return '刚刚';
  if (sec < 3600) return Math.floor(sec / 60) + ' 分钟前';
  if (sec < 86400) return Math.floor(sec / 3600) + ' 小时前';
  return Math.floor(sec / 86400) + ' 天前';
}

// 重新扫码：走的是和「扫码登录」完全相同的流程。
// C# 侧扫码成功后会按账号匹配已有账号并替换其凭据（App.CompleteQrLoginAsync 里已有该逻辑），
// 所以这里不需要特殊参数，扫同一个账号即可。
function rescanAccount(id) {
  const a = (state.accounts || []).find(x => x.id === id);
  navigate('qr');
  send({ type: 'start-qr' });
  showToast('请用希沃 App 扫码登录' + (a && a.displayName ? '（' + a.displayName + '）' : '') + '，登录成功后会自动替换该账号的凭据', 'info');
}

function switchToActive(id) {
  send({type:'move-to-active', id});
}
function switchToInactive(id) {
  send({type:'move-to-inactive', id});
}

function selectAccount(id) {
  state.selectedId = state.selectedId === id ? null : id;
  // 只改选中态，不重建整个列表 —— 重建会让所有卡片重新插入 DOM，看着像整列刷新了一遍
  document.querySelectorAll('.acct-card').forEach(function (el) {
    el.classList.toggle('selected', el.getAttribute('data-id') === state.selectedId);
  });
  updateActionBar();
}

function updateActionBar() {
  const sel = state.selectedId;
  const has = !!sel && (state.accounts||[]).some(a => a.id === sel);
  ['actEditName','actEditTags','actRenew','actDelete','actMoveUp','actMoveDown'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.disabled = !has;
  });
}

// ===== Action Handlers =====
function actEditName() {
  if (!state.selectedId) return;
  const acct = state.accounts.find(a => a.id === state.selectedId);
  // WebView2 不支持 window.prompt（点了不会弹任何东西，静默失败），交给 C# 用原生输入框
  send({ type: 'rename-account-dialog', id: acct ? acct.id : '' });
  const name = null;   // 下面原有的校验逻辑保留，但不会再走到
  if (name && name.trim()) send({type:'edit-display-name', id:state.selectedId, name:name.trim()});
}
function actEditTags() {
  if (!state.selectedId) return;
  // 标签输入交给 C# 侧的原生输入框：WebView2 不支持 window.prompt，
  // 用 prompt 会出现「点了『标签』毫无反应」的静默失败。
  send({type:'edit-tags-dialog', id:state.selectedId});
}
function actRenew() {
  if (!state.selectedId) return;
  send({ type: 'renew-account', id: state.selectedId });
  showToast('正在续期该账号…', 'info');
}
function actRenewAll() {
  send({ type: 'renew-account', id: '' });
  showToast('正在续期所有账号…', 'info');
}
function actDelete() {
  if (!state.selectedId) return;
  // 确认统一交给 C#（原生对话框，默认焦点在「否」）：
  // 这里再 confirm 一次会出现两个确认框，点掉第一个之后还要再点一次。
  send({type:'delete-account', id:state.selectedId});
}

// ===== 事件委托：账号卡片 / 生效切换（替代内联 onclick 字符串拼接，避免单引号注入）=====
document.addEventListener('click', function(e) {
  const t = e.target;
  if (!t || !t.closest) return;
  const sw = t.closest('.switch-btn');
  if (sw) {
    const card = sw.closest('.acct-card');
    const id = card ? card.getAttribute('data-id') : '';
    if (!id || sw.disabled || sw.classList.contains('disabled')) return;
    // 重新扫码按钮同样带 .switch-btn（沿用同一套样式），所以要先判断它
    if (sw.hasAttribute('data-rescan')) { rescanAccount(id); return; }
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

bindClick('actMoveUp', function() { moveSelected(-1); });
bindClick('actMoveDown', function() { moveSelected(1); });

// ===== 拖拽排序：同栏内调整顺序，拖到另一栏即切换生效/未生效 =====
// 比「上移/下移/移到生效」三个按钮直观得多：看到哪儿就放到哪儿。
function initAccountDrag() {
  ['activeCol', 'inactiveCol'].forEach(function (colId) {
    var col = document.getElementById(colId);
    if (!col || col.__dragBound) return;
    col.__dragBound = true;

    col.addEventListener('dragstart', function (e) {
      var card = e.target.closest ? e.target.closest('.acct-card') : null;
      if (!card) return;
      card.classList.add('dragging');
      try {
        e.dataTransfer.setData('text/plain', card.getAttribute('data-id'));
        e.dataTransfer.effectAllowed = 'move';
      } catch (_) { }
    });

    col.addEventListener('dragend', function () {
      document.querySelectorAll('.acct-card.dragging').forEach(function (el) { el.classList.remove('dragging'); });
      document.querySelectorAll('.acct-card.drop-before,.acct-card.drop-after').forEach(function (el) {
        el.classList.remove('drop-before', 'drop-after');
      });
    });

    col.addEventListener('dragover', function (e) {
      var dragging = document.querySelector('.acct-card.dragging');
      if (!dragging) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      document.querySelectorAll('.acct-card.drop-before,.acct-card.drop-after').forEach(function (el) {
        el.classList.remove('drop-before', 'drop-after');
      });
      var card = e.target.closest ? e.target.closest('.acct-card') : null;
      if (card && card !== dragging) {
        var rect = card.getBoundingClientRect();
        card.classList.add((e.clientY - rect.top) > rect.height / 2 ? 'drop-after' : 'drop-before');
      }
    });

    col.addEventListener('drop', function (e) {
      var dragging = document.querySelector('.acct-card.dragging');
      if (!dragging) return;
      e.preventDefault();
      var card = e.target.closest ? e.target.closest('.acct-card') : null;
      if (card && card !== dragging) {
        var after = card.classList.contains('drop-after');
        card.insertAdjacentElement(after ? 'afterend' : 'beforebegin', dragging);
      } else if (!card) {
        col.appendChild(dragging);
      }
      // 栏目在 DOM 里的先后就是「生效 → 未生效」，所以直接按文档顺序读出来的就是新列表
      var ids = Array.from(document.querySelectorAll('#activeCol .acct-card, #inactiveCol .acct-card'))
        .map(function (el) { return el.getAttribute('data-id'); })
        .filter(function (x) { return !!x; });
      if (ids.length) send({ type: 'reorder-accounts', ids: ids });
    });
  });
}
