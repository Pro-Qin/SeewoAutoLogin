// troubleshoot.js — 疑难解答页：自助体检、常见问题、环境摘要与 issue 入口；以及侧栏「启动希沃」。依赖：core.js、ui.js。

// ===== 侧栏：启动希沃 =====
// 状态灯只表达一件事：希沃在不在跑。用户扫一眼侧栏就知道「现在点了会不会重复开一个」。
function renderSeewoState(msg) {
  const dot = document.getElementById('seewoRunDot');
  const text = document.getElementById('seewoRunText');
  const btn = document.getElementById('launchSeewoBtn');
  const running = !!(msg && msg.running);

  if (dot) dot.classList.toggle('running', running);
  if (btn) btn.classList.toggle('running', running);
  if (text) {
    text.textContent = running
      ? (msg.version ? '运行中 · ' + msg.version : '运行中')
      : t('troubleshoot.seewoIdle', '未检测到运行');
  }
}

function launchSeewo() {
  const btn = document.getElementById('launchSeewoBtn');
  if (btn) btn.classList.add('pending');
  send({ type: 'launch-seewo' });
  // 兜底：希沃从点击到出现主窗口可能要几秒，别让按钮一直卡在 pending
  setTimeout(function () { if (btn) btn.classList.remove('pending'); }, 4000);
}

// ===== 自助体检 =====
// 体检项由 C# 采集（它才知道网关端口、hosts 状态、自启方式），前端只负责呈现。
// 每一项都带「为什么」和「怎么办」：只给结论的诊单等于没给。
function loadTroubleshoot() {
  const list = document.getElementById('tsCheckList');
  if (list && !list.dataset.loaded) list.innerHTML = '<div class="ts-empty">' + esc(t('troubleshoot.checking', '正在体检…')) + '</div>';
  send({ type: 'troubleshoot-snapshot' });
}

function tsStateIcon(state) {
  if (state === 'ok') return '✓';
  if (state === 'warn') return '!';
  return '×';
}

function renderTroubleshoot(msg) {
  const list = document.getElementById('tsCheckList');
  const summary = document.getElementById('tsSummary');
  if (!list) return;

  const items = (msg && msg.items) || [];
  if (!items.length) {
    list.innerHTML = '<div class="ts-empty">' + esc((msg && msg.error) || '体检失败，请稍后重试') + '</div>';
    if (summary) summary.textContent = '';
    return;
  }

  list.dataset.loaded = '1';
  let ok = 0;
  list.innerHTML = items.map(function (item) {
    const state = item.state || 'warn';
    if (state === 'ok') ok++;
    const fix = item.fix
      ? '<button class="ts-fix" data-fix="' + escAttr(item.fix) + '">' + esc(item.fix === 'repair' ? '一键修复' : '启动希沃') + '</button>'
      : '';
    return '<div class="ts-check ts-' + escAttr(state) + '">' +
      '<span class="ts-check-dot" aria-hidden="true">' + tsStateIcon(state) + '</span>' +
      '<div class="ts-check-body">' +
        '<div class="ts-check-line"><b>' + esc(item.title) + '</b>' +
          '<span class="ts-check-value">' + esc(item.value) + '</span></div>' +
        '<div class="ts-check-detail">' + esc(item.detail) + '</div>' +
      '</div>' + fix + '</div>';
  }).join('');

  if (summary) {
    summary.textContent = ok === items.length
      ? '全部通过 · ' + items.length + ' 项'
      : (items.length - ok) + ' 项需要注意 · 通过 ' + ok + '/' + items.length;
    summary.className = 'ts-summary' + (ok === items.length ? ' ok' : '');
  }
}

function runTroubleshootFix(action) {
  if (action === 'repair') { repairSso(); return; }
  if (action === 'launch-seewo') { launchSeewo(); return; }
}

// ===== 常见问题 =====
// 用原生 <details>：键盘可达、可被浏览器查找、零 JS 依赖。
const TS_FAQ = [
  {
    q: '希沃登录页没出现「快捷登录」入口',
    a: '多半是本机网关没起来或 hosts 没写进去。先回上面「自动体检」看红项，点「一键修复」；仍有问题就重启本程序，再不行确认是否以管理员身份运行。'
  },
  {
    q: '提示「凭据已过期」，但重启希沃后又正常登录',
    a: '旧版本把扫码凭据当成唯一依据，密码续期成功了前台仍报过期，1.14.27 起已修。仍在提示的话，到账号列表点「立即巡检」，看具体哪个账号、报什么错。'
  },
  {
    q: '换了电脑或重装系统，导入配置后账号全部不可用',
    a: '密码用 Windows 的 DPAPI 加密，密钥绑定当前用户，换机器就解不开 —— 这不是密码错了。导入时把密码列改填明文，或导入后在账号列表逐个补密码。'
  },
  {
    q: '扫码登录的账号，过几天就要重新扫',
    a: '扫码凭据同样绑在本机用户上，清理缓存、换机器、换系统用户都会丢。这类账号建议直接改用密码登录：密码续期由程序自动完成，不依赖浏览器会话。'
  },
  {
    q: '开机后程序没有自己起来',
    a: '计划任务被安全软件拦下，或任务指向了旧路径。到「设置 → 启动」把开机自启关掉再打开，重建一次；同时给本程序在安全软件里加白名单。'
  },
  {
    q: '更新下载到一半失败',
    a: '直连 GitHub 不稳定。到「设置」打开「下载加速」再重试；仍失败就去仓库的 Releases 页手动下载安装包，覆盖安装即可，账号与配置不受影响。'
  },
  {
    q: '杀毒软件报毒或被拦截',
    a: '程序需要改 hosts、建计划任务、监听本机登录请求，这些行为在某些规则下会被误判。请只从官方 Releases 页下载，并把程序目录加进白名单。'
  },
  {
    q: '点了按钮没反应，界面像卡住了',
    a: '打开「诊断日志」看最后一条以 [WebView] 开头的记录，那里会写清楚是被拦截还是抛了异常。把这段日志连同环境摘要一起发到 issue，定位会快很多。'
  }
];

function renderFaq() {
  const box = document.getElementById('tsFaq');
  if (!box || box.dataset.rendered) return;
  box.dataset.rendered = '1';
  box.innerHTML = TS_FAQ.map(function (item) {
    return '<details class="ts-faq-item">' +
      '<summary>' + esc(item.q) + '</summary>' +
      '<div class="ts-faq-body">' + esc(item.a) + '</div>' +
      '</details>';
  }).join('');
}

// ===== 绑定 =====
bindClick('launchSeewoBtn', launchSeewo);
bindClick('tsRefreshBtn', loadTroubleshoot);
bindClick('tsCopyBtn', function () { send({ type: 'copy-support-report' }); });
bindClick('tsIssueBtn', function () { send({ type: 'report-issue' }); });

(function bindTroubleshootList() {
  const list = document.getElementById('tsCheckList');
  if (!list) return;
  list.addEventListener('click', function (e) {
    const btn = e.target && e.target.closest ? e.target.closest('.ts-fix') : null;
    if (btn) runTroubleshootFix(btn.getAttribute('data-fix'));
  });
})();

renderFaq();
// 侧栏状态灯进页面就要有值，不等用户点进疑难解答
send({ type: 'get-seewo-state' });
