namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>登录记录：按账号查「什么时候登录过、谁触发的、成没成」</summary>
        private async Task HandleGetLoginHistory(JsonElement root)
        {
            try
            {
                var accountId = root.Str("accountId");
                var source = root.Str("source");
                var limit = root.Int("limit", 200);

                var records = LoginHistoryService.Query(accountId, limit, source);
                var accounts = _app.Config.Accounts
                    .Select(x => new { id = x.Id, name = x.DisplayName ?? x.Username ?? x.Id })
                    .ToList();

                await SendToJs(new
                {
                    type = "login-history",
                    filter = accountId ?? "",
                    source = source ?? "",
                    accounts,
                    items = records.Select(r => new
                    {
                        accountId = r.AccountId,
                        time = DateTimeOffset.TryParse(r.AtUtc, out var at)
                            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                            : r.AtUtc,
                        name = string.IsNullOrWhiteSpace(r.Name) ? r.Username : r.Name,
                        source = LoginHistoryService.DescribeSource(r.Source),
                        success = r.Success,
                        detail = r.Detail
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[History] 读取登录记录失败: {ex.GetType().Name} - {ex.Message}");
                await SendToJs(new { type = "login-history", items = new object[0], accounts = new object[0], error = ex.Message });
            }
        }

        /// <summary>一键修复：重写 hosts 映射 + 重启 SSO 网关，并回传最新自检状态</summary>
        private async Task HandleRepairSsoAsync()
        {
            var text = await _app.RepairSsoAsync();
            await SendToJs(new { type = "toast", text, level = "info" });
            await SendToJs(new { type = "status", status = _app.BuildSelfCheckStatus() });
            await RefreshAccountList();
        }

        /// <summary>账号健康巡检：逐个验证密码/扫码令牌，并把结果回传前端</summary>
        private async Task HandleHealthCheckAsync()
        {
            await SendToJs(new { type = "health", running = true, results = Array.Empty<object>() });
            try
            {
                await _app.RunHealthCheckAsync();
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Health] 巡检异常: {ex.Message}");
            }

            var results = _app.Config.Accounts.Select(a => new
            {
                id = a.Id,
                state = string.IsNullOrEmpty(a.HealthState) ? "unknown" : a.HealthState,
                message = a.HealthMessage ?? ""
            }).ToList();
            await SendToJs(new { type = "health", running = false, results });
            await RefreshAccountList();
        }

        /// <summary>
        /// 导出诊断包：日志 + 脱敏配置 + 系统信息 + 自检快照 → 一个 zip。
        /// 收集与压缩放在后台线程，避免日志较多时把界面卡住；任何一项失败都只是少一个文件。
        /// </summary>
        private async Task HandleExportDiagnosticsAsync()
        {
            DiagnosticBundleContext context = null;
            try
            {
                var dataDir = App.DataDirectory;
                context = new DiagnosticBundleContext
                {
                    AppDataDir = dataDir,
                    LogsDir = Path.Combine(dataDir, "Logs"),
                    ConfigPath = Path.Combine(dataDir, "config.json"),
                    AppVersion = AppVersion,
                    SelfCheckSnapshot = () => _app.BuildSelfCheckStatus(),
                    Log = _app.WriteDiagnosticLog
                };

                var targetPath = DiagnosticBundleService.AskSavePath(this, context, out var cancelled);
                if (cancelled)
                {
                    await SendToJs(new { type = "diagnostics-exported", ok = false, path = "", message = "已取消导出" });
                    return;
                }

                var result = await Task.Run(() => DiagnosticBundleService.Create(targetPath, context));
                await SendToJs(new
                {
                    type = "diagnostics-exported",
                    ok = result.Ok,
                    path = result.Path ?? "",
                    message = result.Message ?? ""
                });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Diagnostics] 导出异常: {ex.GetType().Name} - {ex.Message}");
                await SendToJs(new
                {
                    type = "diagnostics-exported",
                    ok = false,
                    path = "",
                    message = "导出失败：" + ex.Message
                });
            }
        }

        /// <summary>
        /// 一键把所有日志导出到桌面：环境摘要 + 运行日志 + 崩溃日志，整合成一个 txt。
        ///
        /// 「导出诊断包」产出的是给开发者看的 zip（含配置与自检），这个更直接 ——
        /// 用户把桌面上的文件拖进聊天窗口发过来就能排查，不用教他去 AppData 里翻目录。
        /// 收集与拼接放后台线程，日志多时不会卡界面。
        /// </summary>
        private async Task HandleExportLogsToDesktopAsync()
        {
            try
            {
                var dataDir = App.DataDirectory;
                var path = await Task.Run(() => LogExportService.ExportToDesktop(dataDir, AppVersion, _app.WriteDiagnosticLog));
                await SendToJs(new
                {
                    type = "logs-exported",
                    ok = true,
                    path = path,
                    message = "日志已导出到桌面"
                });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[LogExport] 导出失败: {ex.GetType().Name} - {ex.Message}");
                await SendToJs(new
                {
                    type = "logs-exported",
                    ok = false,
                    path = "",
                    message = "导出失败：" + ex.Message
                });
            }
        }
    }
}
