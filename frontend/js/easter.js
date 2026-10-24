// easter.js — 关于页彩蛋：键盘敲 qinzzq 触发彩带。依赖：core.js（state）、ui.js。必须最后加载。

(function bindEasterEgg() {
  const SECRET = 'qinzzq';
  let buffer = '';

  document.addEventListener('keydown', function (e) {
    if (!e.key || e.key.length !== 1) return;      // 只吃可见字符，忽略方向键 / Ctrl 组合
    if (e.ctrlKey || e.altKey || e.metaKey) return;

    const ch = e.key.toLowerCase();
    if (ch < 'a' || ch > 'z') { buffer = ''; return; }

    buffer = (buffer + ch).slice(-SECRET.length);
    if (buffer !== SECRET) return;
    buffer = '';

    // 只在关于页生效：其他地方敲键盘是正常输入
    if (typeof state === 'object' && state.currentPage !== 'about') return;
    launchConfetti();
  });

  function launchConfetti() {
    if (document.getElementById('confettiCanvas')) return;   // 连敲不叠层

    const canvas = document.createElement('canvas');
    canvas.id = 'confettiCanvas';
    canvas.className = 'confetti-canvas';
    document.body.appendChild(canvas);

    const ctx = canvas.getContext('2d');
    const dpr = window.devicePixelRatio || 1;
    const resize = function () {
      canvas.width = window.innerWidth * dpr;
      canvas.height = window.innerHeight * dpr;
      canvas.style.width = window.innerWidth + 'px';
      canvas.style.height = window.innerHeight + 'px';
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();

    const colors = ['#00C880', '#4ADE80', '#5AB0FF', '#F5C451', '#FF7A64', '#A78BFA'];
    const pieces = [];
    for (let i = 0; i < 160; i++) {
      pieces.push({
        x: window.innerWidth * (0.15 + Math.random() * 0.7),
        y: -20 - Math.random() * 120,
        vx: (Math.random() - 0.5) * 3.4,
        vy: 1.8 + Math.random() * 3.2,
        w: 5 + Math.random() * 5,
        h: 8 + Math.random() * 8,
        rot: Math.random() * Math.PI,
        vr: (Math.random() - 0.5) * 0.22,
        color: colors[(Math.random() * colors.length) | 0]
      });
    }

    const started = performance.now();
    function frame(now) {
      const elapsed = now - started;
      ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
      pieces.forEach(function (p) {
        p.vy += 0.05;                      // 重力
        p.vx *= 0.995;
        p.x += p.vx;
        p.y += p.vy;
        p.rot += p.vr;
        ctx.save();
        ctx.translate(p.x, p.y);
        ctx.rotate(p.rot);
        ctx.globalAlpha = elapsed > 2600 ? Math.max(0, 1 - (elapsed - 2600) / 900) : 1;
        ctx.fillStyle = p.color;
        ctx.fillRect(-p.w / 2, -p.h / 2, p.w, p.h);
        ctx.restore();
      });

      if (elapsed < 3500) {
        requestAnimationFrame(frame);
      } else {
        canvas.remove();
      }
    }
    requestAnimationFrame(frame);
  }
})();
