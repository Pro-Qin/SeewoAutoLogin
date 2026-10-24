// ui.js — 通用工具与轻提示（esc / escAttr / toastLevelClass / showToast）。依赖：core.js。

// ===== Helpers =====
function esc(s) { if(!s) return ''; return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
function escAttr(s) { return esc(s).replace(/'/g, '&#39;'); }

// ===== Toast（右下角轻提示，2.5s 自动消失）=====
function toastLevelClass(level) {
  const l = String(level || '').toLowerCase();
  if (l === 'error' || l === 'err' || l === 'danger' || l === 'failed' || l === 'fail') return 'toast-error';
  if (l === 'warn' || l === 'warning') return 'toast-warn';
  if (l === 'ok' || l === 'success') return 'toast-ok';
  return 'toast-info';
}
function showToast(text, level) {
  if (text === undefined || text === null || text === '') return;
  let box = document.getElementById('toastContainer');
  if (!box) {
    box = document.createElement('div');
    box.id = 'toastContainer';
    box.className = 'toast-container';
    document.body.appendChild(box);
  }
  const el = document.createElement('div');
  el.className = 'toast ' + toastLevelClass(level);
  el.textContent = String(text);
  box.appendChild(el);
  setTimeout(function() {
    el.classList.add('hide');
    setTimeout(function() { if (el.parentNode) el.parentNode.removeChild(el); }, 260);
  }, 2500);
}

// ===== 网页级浮窗 =====
// 原生 title 的提示框由浏览器绘制：延迟约 1 秒、样式不可控、还会被窗口边缘裁掉。
// 这里统一接管 [data-tip]，用页面内的定位元素，跟谁家的主题都一致。
(function bindTooltips() {
  let tip = null;
  function ensure() {
    if (tip) return tip;
    tip = document.createElement('div');
    tip.className = 'ui-tip';
    tip.hidden = true;
    document.body.appendChild(tip);
    return tip;
  }
  document.addEventListener('mouseover', function (e) {
    const t = (e.target && e.target.closest) ? e.target.closest('[data-tip]') : null;
    if (!t) return;
    const text = t.getAttribute('data-tip') || '';
    if (!text) return;
    const el = ensure();
    el.textContent = text;
    el.hidden = false;
    const r = t.getBoundingClientRect();
    const w = el.offsetWidth, h = el.offsetHeight;
    let x = r.left + r.width / 2 - w / 2;
    let y = r.top - h - 8;
    if (y < 8) y = r.bottom + 8;
    x = Math.max(8, Math.min(x, window.innerWidth - w - 8));
    el.style.transform = 'translate3d(' + Math.round(x) + 'px,' + Math.round(y) + 'px,0)';
  });
  document.addEventListener('mouseout', function (e) {
    const t = (e.target && e.target.closest) ? e.target.closest('[data-tip]') : null;
    if (!t || !tip) return;
    tip.hidden = true;
  });
  document.addEventListener('scroll', function () { if (tip) tip.hidden = true; }, true);
})();
