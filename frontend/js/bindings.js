// bindings.js — 关于页与维护区的静态控件绑定（外链、协议入口、恢复出厂、一键修复、健康巡检、批量/CSV 导入、备份、标签清除）。依赖：以上全部模块。

// ===== 受信任外链：作者主页 / 项目仓库 =====
// 前端只负责发起，实际打开由 C# 侧按域名白名单再校验一次后交给系统浏览器
function openExternalUrl(url) {
  send({ type: 'open-external', url: url });
}
bindClick('authorLink', function () { openExternalUrl('https://space.bilibili.com/1849305981'); });
bindClick('madeByLink', function () { openExternalUrl('https://space.bilibili.com/1849305981'); });
bindClick('openRepoBtn', function () { openExternalUrl('https://github.com/Pro-Qin/SeewoAutoLogin'); });
bindClick('openSiteBtn', function () { openExternalUrl('https://pro-qin.github.io/SeewoAutoLogin/'); });



bindClick('factoryResetBtn', function () {
  if (!confirm('恢复出厂设置会清空所有账号、扫码凭据与全部设置，且不可撤销。\n\n确定继续吗？')) return;
  const el = document.getElementById('factoryResetStatus');
  if (el) el.textContent = '正在恢复，完成后会自动重启程序…';
  send({ type: 'factory-reset' });
  // 兜底：万一后台没响应，给出可操作的提示，而不是一直停在“正在恢复”
  setTimeout(function () {
    if (el && el.textContent.indexOf('正在恢复') === 0) {
      el.textContent = '恢复超时：请在托盘图标上右键 → 退出程序，然后重新打开。';
    }
  }, 20000);
});

bindClick('repairBtn', function() { repairSso(); });
bindClick('actHealthCheck', function() { runHealthCheck(); });
bindClick('batchImportBtn', function() { batchImport(); });
bindClick('importCsvBtn', function() { importCsvFile(); });
// 标签筛选的「清除」：下拉框右侧的 × 和提示条里的按钮都归零到「全部」
bindClick('tagFilterClear', function() { setTagFilter(''); });
bindClick('listBackupsBtn', function() { listBackups(); });
bindClick('backupModalClose', function() { closeBackupModal(); });
