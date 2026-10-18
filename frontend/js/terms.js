// terms.js — 用户协议弹窗与遮罩。依赖：core.js、ui.js。

// ===== 用户协议：关于页点击后全屏弹窗展示 =====
// 正文由 C# 从嵌入资源下发（Resources/terms.txt），与欢迎界面的协议页共用同一份，避免两处内容不一致
function renderTerms(text) {
  const NL = String.fromCharCode(10);
  const body = document.getElementById('termsBody');
  if (!body) return;
  body.innerHTML = '';
  const frag = document.createDocumentFragment();
  (text || '').split(NL).forEach(function (raw) {
    const line = raw.trimEnd();
    const t = line.trim();
    const div = document.createElement('div');
    if (!t) { div.className = 't-gap'; }
    else if (t.indexOf('SeewoAutoLogin') === 0 || t.indexOf('用户协议') === 0) { div.className = 't-title'; div.textContent = t; }
    else if (t.indexOf('版本 ') === 0 || t.indexOf('（本协议全文完）') === 0) { div.className = 't-meta'; div.textContent = t; }
    else if (/^(第[一二三四五六七八九十]+条|附：)/.test(t)) { div.className = 't-head'; div.textContent = t; }
    else if (/^【/.test(t)) { div.className = 't-warn'; div.textContent = t; }
    else if (/^[0-9]+[.][0-9]+/.test(t)) { div.className = 't-clause'; div.textContent = t; }
    else if (/^　/.test(line)) { div.className = 't-sub'; div.textContent = t; }
    else { div.className = 't-p'; div.textContent = t; }
    frag.appendChild(div);
  });
  body.appendChild(frag);
  body.scrollTop = 0;
}

function openTerms() {
  const ov = document.getElementById('termsOverlay');
  if (!ov) return;
  ov.hidden = false;
  if (window.__termsText) {
    renderTerms(window.__termsText);
  } else {
    renderTerms('正在载入协议正文…');
    send({ type: 'get-terms' });
  }
}

function closeTerms() {
  const ov = document.getElementById('termsOverlay');
  if (ov) ov.hidden = true;
}

bindClick('termsLink', openTerms);
bindClick('termsCloseBtn', closeTerms);
(function bindTermsOverlay() {
  const ov = document.getElementById('termsOverlay');
  if (!ov) return;
  // 点遮罩空白处关闭；点面板内部不关闭
  ov.addEventListener('click', function (e) { if (e.target === ov) closeTerms(); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !ov.hidden) closeTerms();
  });
})();
