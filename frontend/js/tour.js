// tour.js — 使用教程聚光灯引导。依赖：core.js、nav.js（navigate）、ui.js。

// ===== 使用教程（聚光灯引导）=====
// 动效约定：遮罩用 clip-path 挖洞（GPU），光圈用 transform + 尺寸过渡，
// 卡片与图片只动 transform/opacity；总时长与 styles.css 中的过渡保持一致。
const TOUR_PAD = 10;
const TOUR_STEPS = [
  {
    step: '第 1 步 · 共 5 步',
    title: '平时从任务栏托盘打开它',
    html: '软件启动后会缩到右下角托盘。双击那个<b>蓝色闪电图标</b>（或右键它）就能随时打开这个管理界面。',
    media: 'assets/tutorial-tray.png',
    mediaClass: 'tray'
  },
  {
    step: '第 2 步 · 共 5 步',
    title: '方式一：右下角的 + 号',
    html: '点右下角的 <b>+ 号</b>会展开两个入口：<b>密码添加</b>（账号密码登录）和 <b>扫码添加</b>（希沃 App 扫码）。',
    focus: '#fabBtn, #fabMenu',
    openFab: true
  },
  {
    step: '第 3 步 · 共 5 步',
    title: '方式二：左侧侧边栏',
    html: '左侧的 <b>添加账号</b> 与 <b>扫码登录</b> 是同样的两个入口，挑顺手的一种用就行。',
    focus: '#navAdd, #navQr'
  },
  {
    step: '第 4 步 · 共 5 步',
    title: '添加后，希沃会变成这样',
    html: '打开希沃白板的登录界面，账号头像会直接排在这里，<b>点一下头像就能登录</b>，不用再输账号密码。',
    media: 'assets/tutorial-seewo.png',
    mediaClass: 'seewo',
    wide: true
  },
  {
    step: '第 5 步 · 共 5 步',
    title: '可以开始用了',
    html: '教程随时能在「<b>设置 → 帮助与维护</b>」里重看一遍。',
    last: true
  }
];

let tourIndex = -1;
let tourTimer = null;

function tourEl(id) { return document.getElementById(id); }

function tourUnionRect(selector) {
  const els = document.querySelectorAll(selector);
  let box = null;
  for (let i = 0; i < els.length; i++) {
    const r = els[i].getBoundingClientRect();
    if (r.width === 0 && r.height === 0) continue;
    if (!box) box = { top: r.top, left: r.left, right: r.right, bottom: r.bottom };
    else {
      box.top = Math.min(box.top, r.top);
      box.left = Math.min(box.left, r.left);
      box.right = Math.max(box.right, r.right);
      box.bottom = Math.max(box.bottom, r.bottom);
    }
  }
  return box;
}

// 把聚光灯（遮罩挖洞 + 发光光圈）移到目标上；selector 为空表示全屏遮罩
function tourSpotlight(selector) {
  const mask = tourEl('tourMask'), ring = tourEl('tourRing'), card = tourEl('tourCard');
  if (!mask || !ring || !card) return;
  const vw = window.innerWidth, vh = window.innerHeight;
  const box = selector ? tourUnionRect(selector) : null;

  if (!box) {
    mask.style.setProperty('--tour-top', '-20px');
    mask.style.setProperty('--tour-right', '-20px');
    mask.style.setProperty('--tour-bottom', '-20px');
    mask.style.setProperty('--tour-left', '-20px');
    ring.classList.remove('show');
    tourPlaceCard(null);
    return;
  }

  const top = Math.max(0, box.top - TOUR_PAD);
  const left = Math.max(0, box.left - TOUR_PAD);
  const right = Math.max(0, vw - box.right - TOUR_PAD);
  const bottom = Math.max(0, vh - box.bottom - TOUR_PAD);

  mask.style.setProperty('--tour-top', top + 'px');
  mask.style.setProperty('--tour-right', right + 'px');
  mask.style.setProperty('--tour-bottom', bottom + 'px');
  mask.style.setProperty('--tour-left', left + 'px');

  ring.style.width = (vw - left - right) + 'px';
  ring.style.height = (vh - top - bottom) + 'px';
  ring.style.transform = 'translate3d(' + left + 'px,' + top + 'px,0)';
  ring.classList.add('show');

  tourPlaceCard({ top: top, right: vw - right, bottom: vh - bottom, left: left });
}

// 卡片优先贴在洞下方；放不下就翻到上方，再不行就居中
function tourPlaceCard(hole) {
  const card = tourEl('tourCard');
  if (!card) return;
  const vw = window.innerWidth, vh = window.innerHeight;
  const cw = card.offsetWidth || 430;
  const ch = card.offsetHeight || 240;
  const gap = 16;

  let x = hole ? hole.left : (vw - cw) / 2;
  let y;

  if (!hole) {
    y = (vh - ch) / 2;
  } else if (hole.bottom + gap + ch <= vh - 16) {
    y = hole.bottom + gap;
  } else if (hole.top - gap - ch >= 16) {
    y = hole.top - gap - ch;
  } else {
    y = Math.max(16, (vh - ch) / 2);
  }

  // 卡片比视口还高时顶到最上面，宁可贴边也不要让「继续/完成」被裁掉
  if (ch > vh - 32) y = 16;

  x = Math.max(16, Math.min(x, vw - cw - 16));
  card.style.transform = 'translate3d(' + Math.round(x) + 'px,' + Math.round(y) + 'px,0)';
  card.classList.add('show');
}

function tourRenderStep(i) {
  tourIndex = i;
  const s = TOUR_STEPS[i];
  if (!s) return;

  const stepEl = tourEl('tourStep'), titleEl = tourEl('tourTitle'), textEl = tourEl('tourText');
  const media = tourEl('tourMedia'), card = tourEl('tourCard');
  const nextBtn = tourEl('tourNextBtn'), hint = tourEl('tourHint');
  if (!stepEl || !titleEl || !textEl || !media || !card) return;

  stepEl.textContent = s.step || '';
  titleEl.textContent = s.title || '';
  textEl.innerHTML = s.html || '';

  media.classList.remove('in');
  media.innerHTML = '';
  if (s.media) {
    const img = document.createElement('img');
    img.alt = '';
    if (s.mediaClass) img.className = s.mediaClass;
    img.addEventListener('load', function () { media.classList.add('in'); });
    img.src = s.media;
    media.appendChild(img);
  }

  card.classList.toggle('wide', !!s.wide);

  // 导航按钮：第一步不显示「上一步」，最后一步把「下一步」换成「完成教学」
  const prevBtn = tourEl('tourPrevBtn');
  if (prevBtn) prevBtn.style.display = i > 0 ? '' : 'none';
  if (nextBtn) nextBtn.textContent = s.last ? '完成教学' : '下一步';
  if (hint) {
    hint.textContent = s.last ? '' : '点击任意地方继续';
    hint.classList.toggle('pulse', !s.last);
  }

  // 只在需要展示加号菜单的步骤展开它，切走后自动收起
  const group = document.getElementById('fabGroup');
  if (group) group.classList.toggle('open', !!s.openFab);

  // 内容变了，先隐藏卡片量好尺寸，再定位淡入
  card.classList.remove('show');
  clearTimeout(tourTimer);
  requestAnimationFrame(function () {
    if (!s.focus) { tourSpotlight(null); return; }
    // 菜单有展开动画，等它稳定后再测量，否则框选范围会按收起状态计算
    tourTimer = setTimeout(function () { tourSpotlight(s.focus); }, s.openFab ? 520 : 60);
  });
}

function startTour() {
  const ov = tourEl('tourOverlay');
  if (!ov) return;

  // 加号悬浮按钮只属于账号列表页：在设置页直接开教程会框不到它（元素不参与布局）。
  // 教程统一在主界面（账号列表）演示，点「显示使用教程」时先切过去。
  if (typeof navigate === 'function') navigate('accounts');

  if (ov.hidden) ov.hidden = false;
  document.body.classList.add('tour-open');
  window.__tourActive = true;
  tourRenderStep(0);
  send({ type: 'tour-started' });
}

function tourNext() {
  if (tourIndex < 0) return;
  if (tourIndex >= TOUR_STEPS.length - 1) { endTour(true); return; }
  tourRenderStep(tourIndex + 1);
}

function tourPrev() {
  if (tourIndex <= 0) return;
  tourRenderStep(tourIndex - 1);
}

function endTour(completed) {
  clearTimeout(tourTimer);
  tourTimer = null;
  tourIndex = -1;
  window.__tourActive = false;

  const ov = tourEl('tourOverlay');
  if (ov) ov.hidden = true;
  document.body.classList.remove('tour-open');

  const ring = tourEl('tourRing');
  if (ring) ring.classList.remove('show');
  const card = tourEl('tourCard');
  if (card) card.classList.remove('show');
  const group = document.getElementById('fabGroup');
  if (group) group.classList.remove('open');

  send({ type: 'tour-done', completed: !!completed });
}

(function bindTour() {
  const ov = tourEl('tourOverlay');
  if (!ov) return;

  ov.addEventListener('click', function (e) {
    if (e.target && e.target.closest &&
        (e.target.closest('#tourSkipBtn') || e.target.closest('#tourNextBtn') || e.target.closest('#tourPrevBtn'))) return;
    tourNext();
  });
  bindClick('tourPrevBtn', function (e) {
    if (e && e.stopPropagation) e.stopPropagation();
    tourPrev();
  });
  bindClick('tourSkipBtn', function (e) {
    if (e && e.stopPropagation) e.stopPropagation();
    endTour(false);
  });
  bindClick('tourNextBtn', function (e) {
    if (e && e.stopPropagation) e.stopPropagation();
    // 每一步都显示「下一步」；到第 5 步时 tourNext() 内部会以“完成”收尾
    tourNext();
  });
  document.addEventListener('keydown', function (e) {
    if (tourIndex < 0) return;
    if (e.key === 'Escape') { e.preventDefault(); endTour(false); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); tourPrev(); }
    else if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowRight') { e.preventDefault(); tourNext(); }
  });
  window.addEventListener('resize', function () {
    if (tourIndex >= 0) tourRenderStep(tourIndex);
  });
})();

bindClick('startTourBtn', function () { startTour(); });
