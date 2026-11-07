using System.Windows;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>issue 选择页：模板选择器（bug / 功能建议 / 使用提问）都挂在这一页上</summary>
        private const string IssueChooserUrl = "https://github.com/Pro-Qin/SeewoAutoLogin/issues/new/choose";

        #region 启动希沃

        /// <summary>侧栏「启动希沃」：已在跑就切前台，不在跑就拉起来</summary>
        private async Task HandleLaunchSeewoAsync()
        {
            var outcome = SeewoLauncher.Launch();

            _app.WriteDiagnosticLog("[Launcher] " + outcome.Message
                + (string.IsNullOrEmpty(outcome.Path) ? "" : "; path=" + outcome.Path));

            await SendToJs(new
            {
                type = "seewo-launch",
                ok = outcome.Ok,
                alreadyRunning = outcome.AlreadyRunning,
                message = outcome.Message
            });
            await SendToJs(new
            {
                type = "toast",
                text = outcome.Message,
                level = outcome.Ok ? "ok" : "error"
            });

            await SendSeewoStateAsync();
        }

        /// <summary>侧栏状态灯与疑难解答页都要知道「希沃在不在跑、什么版本」</summary>
        private async Task HandleGetSeewoStateAsync() => await SendSeewoStateAsync();

        private async Task SendSeewoStateAsync()
        {
            var running = SeewoLauncher.IsRunning();
            string version;
            try
            {
                version = SeewoVersionMonitor.TryGetVersion(out _) ?? "";
            }
            catch
            {
                version = "";
            }

            await SendToJs(new { type = "seewo-state", running, version });
        }

        #endregion

        #region 疑难解答

        /// <summary>
        /// 疑难解答页的自查清单。
        ///
        /// 这里的每一项都必须是「用户能自己动手改的东西」，否则清单只会变成焦虑源：
        /// 每项都给出当前值 + 不通过时该怎么办，能一键修的直接给修的动作。
        /// </summary>
        private async Task HandleTroubleshootSnapshotAsync()
        {
            try
            {
                var status = ToJson(_app.BuildSelfCheckStatus());
                var items = new List<object>();

                var gatewayRunning = Flag(status, "gatewayRunning");
                var gatewayPort = Number(status, "gatewayPort");
                var expectedPort = Number(status, "expectedPort");
                var gatewayPortOk = Flag(status, "gatewayPortOk");
                items.Add(new
                {
                    key = "gateway",
                    title = "SSO 登录网关",
                    state = !gatewayRunning ? "bad" : (gatewayPortOk ? "ok" : "warn"),
                    value = gatewayRunning
                        ? "运行中 · 端口 " + gatewayPort
                        : "未运行",
                    detail = !gatewayRunning
                        ? "网关没起来，希沃登录页不会出现「快捷登录」。点「一键修复」重建网关。"
                        : (gatewayPortOk
                            ? "希沃可以正常请求到本机网关。"
                            : "端口与希沃固定请求的 " + expectedPort + " 不一致，快捷登录入口不会显示，点「一键修复」。"),
                    fix = gatewayRunning && gatewayPortOk ? "" : "repair"
                });

                var hostsOk = Flag(status, "hostsOk");
                items.Add(new
                {
                    key = "hosts",
                    title = "域名映射（hosts）",
                    state = hostsOk ? "ok" : "bad",
                    value = hostsOk ? "已写入" : "缺失",
                    detail = hostsOk
                        ? "希沃登录请求已被指向本机。"
                        : "缺少映射时登录请求会直接打到希沃服务器，快捷登录无法接管。点「一键修复」。",
                    fix = hostsOk ? "" : "repair"
                });

                var autoStartEnabled = Flag(status, "autoStartEnabled");
                var autoStartMode = Text(status, "autoStartMode");
                items.Add(new
                {
                    key = "autostart",
                    title = "开机自动启动",
                    state = autoStartEnabled ? "ok" : "warn",
                    value = autoStartEnabled
                        ? (string.IsNullOrEmpty(autoStartMode) ? "已启用" : "已启用（" + autoStartMode + "）")
                        : "未启用",
                    detail = autoStartEnabled
                        ? "开机后会自动运行，开机不登录也不会断。"
                        : "没启用的话，重启电脑后需要手动打开本程序。到「设置 → 启动」里打开。",
                    fix = ""
                });

                var isAdmin = Flag(status, "isAdmin");
                items.Add(new
                {
                    key = "admin",
                    title = "管理员权限",
                    state = isAdmin ? "ok" : "warn",
                    value = isAdmin ? "是" : "否",
                    detail = isAdmin
                        ? "可以改写 hosts 与系统级计划任务。"
                        : "非管理员时 hosts 与系统级自启可能写不进去，登录入口偶尔会消失。可右键以管理员身份重新打开。",
                    fix = ""
                });

                var webView2 = WebView2Runtime.GetInstalledVersion();
                items.Add(new
                {
                    key = "webview2",
                    title = "WebView2 运行时",
                    state = string.IsNullOrEmpty(webView2) ? "bad" : "ok",
                    value = string.IsNullOrEmpty(webView2) ? "未安装" : webView2,
                    detail = string.IsNullOrEmpty(webView2)
                        ? "界面依赖它渲染，缺失时窗口会是空白。请安装「Microsoft Edge WebView2 Runtime」。"
                        : "界面渲染组件正常。",
                    fix = ""
                });

                var seewoRunning = Flag(status, "seewoRunning");
                var seewoExe = seewoRunning ? null : SeewoLauncher.FindExecutable();
                items.Add(new
                {
                    key = "seewo",
                    title = "希沃白板客户端",
                    state = seewoRunning ? "ok" : (string.IsNullOrEmpty(seewoExe) ? "bad" : "warn"),
                    value = seewoRunning ? "运行中" : (string.IsNullOrEmpty(seewoExe) ? "未找到" : "已安装 · 未运行"),
                    detail = seewoRunning
                        ? "希沃正在运行。"
                        : (string.IsNullOrEmpty(seewoExe)
                            ? "没有找到希沃的安装位置，可能没装或装在非常规目录。先装好希沃白板再回来。"
                            : "点「启动希沃」直接打开，不用去桌面找图标。"),
                    fix = seewoRunning || string.IsNullOrEmpty(seewoExe) ? "" : "launch-seewo"
                });

                var total = 0;
                var healthy = 0;
                foreach (var account in _app.Config.Accounts)
                {
                    total++;
                    if (string.Equals(account.HealthState, "ok", StringComparison.OrdinalIgnoreCase)) healthy++;
                }

                items.Add(new
                {
                    key = "accounts",
                    title = "账号凭据",
                    state = total == 0 ? "warn" : (healthy == total ? "ok" : (healthy == 0 ? "bad" : "warn")),
                    value = total == 0 ? "还没有账号" : "共 " + total + " 个 · 正常 " + healthy,
                    detail = total == 0
                        ? "先在「添加账号」里登录一次，程序才能替你自动登录。"
                        : (healthy == total
                            ? "全部账号的凭据都能用。"
                            : "有账号的密码解不开或凭据已失效。到账号列表点「立即巡检」看具体是哪个，再按提示补密码或重扫。"),
                    fix = ""
                });

                await SendToJs(new { type = "troubleshoot", items });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog("[Support] 生成自查清单失败: " + ex.GetType().Name + " - " + ex.Message);
                await SendToJs(new { type = "troubleshoot", items = new object[0], error = ex.Message });
            }
        }

        /// <summary>
        /// 复制环境摘要。只拷到剪贴板、不打开浏览器：
        /// 用户可能想先补一句自己的描述再一起提交，强行开页面反而打断他。
        /// </summary>
        private async Task HandleCopyIssueReportAsync()
        {
            var copied = TryCopyIssueReport();
            await SendToJs(new
            {
                type = "toast",
                text = copied ? "环境摘要已复制到剪贴板" : "复制失败，剪贴板被其他程序占用，稍后再试",
                level = copied ? "ok" : "warn"
            });
        }

        /// <summary>复制摘要 + 打开 issue 选择页：动手门槛越低，收到的报障就越完整</summary>
        private async Task HandleReportIssueAsync()
        {
            var copied = TryCopyIssueReport();

            try
            {
                Process.Start(new ProcessStartInfo { FileName = IssueChooserUrl, UseShellExecute = true });
                await SendToJs(new
                {
                    type = "toast",
                    text = copied
                        ? "正在打开 GitHub，环境摘要已复制，粘贴到正文即可"
                        : "正在打开 GitHub（摘要复制失败，请手动描述环境）",
                    level = copied ? "ok" : "warn"
                });
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog("[Support] 打开 issue 页失败: " + ex.Message);
                await SendToJs(new
                {
                    type = "toast",
                    text = "打不开浏览器，请手动访问 " + IssueChooserUrl,
                    level = "error"
                });
            }
        }

        private bool TryCopyIssueReport()
        {
            try
            {
                var report = SupportInfo.BuildIssueReport(AppVersion, _app.BuildSelfCheckStatus(), _app.Config.Accounts);
                // 剪贴板被别的进程占着时会抛 COMException，等一会儿再试比直接失败体面
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Clipboard.SetText(report);
                        _app.WriteDiagnosticLog("[Support] 环境摘要已复制; length=" + report.Length);
                        return true;
                    }
                    catch
                    {
                        Thread.Sleep(80);
                    }
                }
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog("[Support] 生成环境摘要失败: " + ex.GetType().Name + " - " + ex.Message);
            }

            return false;
        }

        private static JsonElement ToJson(object value)
        {
            if (value == null) return default;
            try
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
                return document.RootElement.Clone();
            }
            catch
            {
                return default;
            }
        }

        private static bool Flag(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

        private static string Text(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : "";

        private static int Number(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : 0;

        #endregion
    }
}
