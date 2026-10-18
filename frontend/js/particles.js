// particles.js — 背景粒子与性能模式（perf-lite 只在这里切换）。依赖：core.js（state）。

// ===== Particle Background =====
let particleCanvas = null, pCtx = null, pAniId = null, pParticles = [];
function initParticles() {
  if (document.getElementById('particleCanvas')) return;
  const canvas = document.createElement('canvas');
  canvas.id = 'particleCanvas';
  canvas.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:0';
  document.body.appendChild(canvas);
  particleCanvas = canvas;
  pCtx = canvas.getContext('2d');
  resizeParticles();
  window.addEventListener('resize', resizeParticles);
  if (state.particles !== false) startParticles();
}
function resizeParticles() {
  if (!particleCanvas) return;
  particleCanvas.width = window.innerWidth;
  particleCanvas.height = window.innerHeight;
}
function startParticles() {
  if (pAniId) return;
  const count = Math.min(80, Math.floor((particleCanvas.width * particleCanvas.height) / 15000));
  pParticles = Array.from({length:count}, () => ({
    x: Math.random() * particleCanvas.width,
    y: Math.random() * particleCanvas.height,
    vx: (Math.random() - 0.5) * 0.5,
    vy: (Math.random() - 0.5) * 0.5,
    r: Math.random() * 1.5 + 0.5
  }));
  function loop() {
    if (!pCtx || !particleCanvas) return;
    pCtx.clearRect(0, 0, particleCanvas.width, particleCanvas.height);
    for (let p of pParticles) {
      p.x += p.vx; p.y += p.vy;
      if (p.x < 0 || p.x > particleCanvas.width) p.vx *= -1;
      if (p.y < 0 || p.y > particleCanvas.height) p.vy *= -1;
      pCtx.beginPath();
      pCtx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      pCtx.fillStyle = 'rgba(255,255,255,0.4)';
      pCtx.fill();
    }
    // 连线
    for (let i = 0; i < pParticles.length; i++) {
      for (let j = i + 1; j < pParticles.length; j++) {
        const dx = pParticles[i].x - pParticles[j].x;
        const dy = pParticles[i].y - pParticles[j].y;
        const dist = Math.sqrt(dx * dx + dy * dy);
        if (dist < 150) {
          pCtx.beginPath();
          pCtx.moveTo(pParticles[i].x, pParticles[i].y);
          pCtx.lineTo(pParticles[j].x, pParticles[j].y);
          pCtx.strokeStyle = `rgba(255,255,255,${0.12 * (1 - dist / 150)})`;
          pCtx.lineWidth = 0.5;
          pCtx.stroke();
        }
      }
    }
    pAniId = requestAnimationFrame(loop);
  }
  loop();
}
function stopParticles() {
  if (pAniId) { cancelAnimationFrame(pAniId); pAniId = null; }
  if (pCtx) pCtx.clearRect(0, 0, particleCanvas.width, particleCanvas.height);
}
function toggleParticles(on, persist) {
  state.particles = on;
  if (on) startParticles(); else stopParticles();
  // 与同页其它开关一样落盘：否则关掉粒子后下次打开界面又回来了
  if (persist !== false) send({ type: 'update-setting', key: 'particleBackground', value: !!on });
}
// 页面加载完自动初始化
if (document.readyState === 'complete') initParticles();
else window.addEventListener('load', initParticles);

// ===== 性能模式 =====
// 关掉耗性能的持续动效与一次性过渡。规则本体在 styles.css 的 body.perf-lite 段
// （一条规则覆盖所有组件）；这里只负责三件事：挂 / 摘那个类、停 / 启粒子、
// 以及让界面记住当前状态，供其它逻辑判断。
function applyPerformanceMode(on) {
  state.performanceMode = !!on;
  document.body.classList.toggle('perf-lite', !!on);

  if (on) {
    // 粒子是持续重绘，开销最大，必须真的停掉（CSS 管不到 canvas）
    stopParticles();
  } else if (state.particles !== false && particleCanvas) {
    startParticles();
  }
}

// 设置页开关：先作用于当前界面，再落盘（与其它开关同一套路）
function setPerformanceMode(on) {
  applyPerformanceMode(on);
  send({ type: 'update-setting', key: 'performanceMode', value: !!on });
  showToast(on ? '性能模式已开启：已关闭大量界面动效' : '性能模式已关闭：动效恢复', 'info');
}
