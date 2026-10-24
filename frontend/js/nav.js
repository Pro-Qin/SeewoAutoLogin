// nav.js — 页面导航、侧栏指示条与白色跟随条、右下角加号 FAB。依赖：core.js、ui.js。

// ===== Navigation =====
function navigate(page) {
  closeFab();
  state.currentPage = page; state.selectedId = null;
  // 供 CSS 判断「当前在哪一页」（右下角加号只在账号列表出现）
  document.body.dataset.page = page;
  document.querySelectorAll('.nav-item').forEach(el => el.classList.toggle('active', el.dataset.page === page));
  document.querySelectorAll('.page').forEach(el => el.classList.toggle('active', el.id === 'page-' + page));
  const t = { accounts:'账号列表', add:'添加账号', qr:'扫码登录', settings:'设置', history:'登录记录', about:'关于' };
  // 注意：操作栏里凡是要「先选中账号」的按钮，都必须出现在 updateActionButtons 的 id 列表里，
  // 否则它会一直停在 HTML 初始的 disabled 上，选中账号也点不动。
  document.getElementById('pageTitle').textContent = t[page] || '';
  if (page === 'accounts') renderAccounts();
  if (page === 'settings') { send({ type: 'get-credential-lifetime' }); send({ type: 'get-switch-pin' }); }
  if (page === 'history') loadLoginHistory();
  moveNavIndicator();
}

// ===== FAB：新增账号（加号旋转 45° + 二级菜单）=====
function closeFab() {
  const group = document.getElementById('fabGroup');
  const btn = document.getElementById('fabBtn');
  if (group) group.classList.remove('open');
  if (btn) { btn.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); }
}
document.addEventListener('click', function(e) {
  // 教程进行中由教程自己控制加号菜单的展开状态，避免点「下一步」时被这里顺手收起
  if (window.__tourActive) return;
  const group = document.getElementById('fabGroup');
  if (group && group.classList.contains('open') && !group.contains(e.target)) closeFab();
});
document.addEventListener('keydown', function(e) { if (e.key === 'Escape') closeFab(); });

// ===== 侧边栏滑动指示条 =====
// 让「当前在哪一页」有连续的空间感：切换时指示条滑过去，而不是硬切。
// ===== 侧边栏白色跟随条 =====
// 鼠标移到哪一项，白条就滑到哪；离开导航区时淡出。
function moveNavHover(target) {
  var nav = document.querySelector('nav');
  var hv = document.getElementById('navHover');
  if (!nav || !hv) return;
  if (!target) { hv.style.opacity = '0'; return; }
  var nr = nav.getBoundingClientRect();
  var tr = target.getBoundingClientRect();
  var hh = Math.min(tr.height * 0.44, 16);
  hv.style.height = hh + 'px';
  hv.style.opacity = '1';
  hv.style.transform = 'translateY(' + (tr.top - nr.top + (tr.height - hh) / 2) + 'px)';
}
function bindNavHover() {
  var nav = document.querySelector('nav');
  if (!nav) return;
  // 用 mousemove + closest 判断，而不是逐项 mouseenter：
  // 鼠标从导航项移到导航区的间隙（gap / padding）时并不会触发 mouseleave，
  // 那样白条就会停在原地不消失。
  nav.addEventListener('mousemove', function (e) {
    var el = e.target && e.target.closest ? e.target.closest('.nav-item') : null;
    moveNavHover(el);
  });
  nav.addEventListener('mouseleave', function () { moveNavHover(null); });
  // 鼠标移出窗口或窗口失去焦点时也收起来
  document.addEventListener('mouseleave', function () { moveNavHover(null); });
  window.addEventListener('blur', function () { moveNavHover(null); });
  // 切换页面时鼠标可能已经不在侧边栏，顺手收一次
  document.querySelectorAll('.nav-item').forEach(function (el) {
    el.addEventListener('click', function () { setTimeout(function () { moveNavHover(null); }, 260); });
  });
}

function moveNavIndicator() {
  var nav = document.querySelector('nav');
  var ind = document.getElementById('navIndicator');
  var active = document.querySelector('.nav-item.active');
  if (!nav || !ind) return;
  if (!active) { ind.style.opacity = '0'; return; }
  var nr = nav.getBoundingClientRect();
  var ar = active.getBoundingClientRect();
  var h = Math.min(ar.height * 0.5, 20);
  ind.style.height = h + 'px';
  ind.style.opacity = '1';
  ind.style.transform = 'translateY(' + (ar.top - nr.top + (ar.height - h) / 2) + 'px)';
}
window.addEventListener('resize', moveNavIndicator);
// 首次加载时把指示条摆到位（字体加载完尺寸才准，所以两帧后再量一次）
