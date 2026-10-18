// boot.js — 启动引导：DOM 就绪后绑定添加页/导航悬停/健康条/登录记录/拖拽，并摆正侧栏指示条。依赖：全部模块，必须最后加载。

function bootNavIndicator() {
  bindAddPage();
  bindNavHover();
  bindHealthBar();
  bindLoginHistory();
  initAccountDrag();
  moveNavIndicator();
  requestAnimationFrame(function () { requestAnimationFrame(moveNavIndicator); });
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bootNavIndicator);
else bootNavIndicator();
