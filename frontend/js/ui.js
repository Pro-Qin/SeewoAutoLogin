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
