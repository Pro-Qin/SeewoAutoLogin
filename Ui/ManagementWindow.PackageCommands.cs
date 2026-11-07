using System.Windows;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>当前等待用户勾选确认的配置包（null 表示没有进行中的导入）。</summary>
        private Services.ConfigPackageService.ImportPreview _pendingPackage;

        /// <summary>
        /// 导出配置包：账号 + 设置一起加密进 .swalcfg。
        /// 口令优先复用本机保存的那一个，没有才让用户设 —— 老师不会愿意每次导出都手打一遍。
        /// </summary>
        private async Task HandleExportPackageAsync()
        {
            // 每次导出都要手输一遍口令：口令存在本机等于「谁坐在这台机器前谁就能导出全部账号」，
            // 强制输入把这道门重新关上。存下来的口令只用于导入时自动尝试。
            var password = PromptSecret("导出配置包",
                "为这次导出输入口令（至少 6 位）。配置包里的账号、手机号与密码都会用它加密，导入时要输入同一个口令：");
            if (password == null) return;
            if (password.Length < 6) { await SendToast("口令至少 6 位", "error"); return; }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Services.ConfigPackageService.FileFilter,
                DefaultExt = Services.ConfigPackageService.FileExtension,
                AddExtension = true,
                FileName = $"SeewoAutoLogin-配置-{DateTime.Now:yyyyMMdd}{Services.ConfigPackageService.FileExtension}"
            };
            if (dialog.ShowDialog() != true) return;

            var accounts = _app.Config.Accounts.Count(a => !a.IsPlaceholder);
            if (!Services.ConfigPackageService.Export(dialog.FileName, _app.Config, AppVersion, password, out var error))
            {
                _app.WriteDiagnosticLog($"[Package] 导出失败: {error}");
                await SendToast("导出失败：" + error, "error");
                return;
            }

            // 记下这次的口令，只为「本机再导入同一个包」时免输；导出本身永远要手输
            Services.PackagePasswordStore.Set(password);
            _app.WriteDiagnosticLog($"[Package] 已导出配置包（{accounts} 个账号）到 {dialog.FileName}");
            await SendToast($"已导出：{accounts} 个账号 + 设置（AES-256 加密）", "ok");
        }

        /// <summary>
        /// 导入配置包：读包 → 生成差异 → 交给界面勾选。
        /// path 为空表示手动选文件；双击 .swalcfg 时由启动参数传入。
        /// </summary>
        private async Task HandleImportPackageAsync(string path = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入配置包",
                    Filter = Services.ConfigPackageService.FileFilter + "|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;
                path = dialog.FileName;
            }

            if (!File.Exists(path)) { await SendToast("文件不存在", "error"); return; }

            // 先试本机保存的口令；不对再问 —— 换机器导入时自然落到询问分支
            var payload = default(Services.ConfigPackageService.PackagePayload);
            var error = "";
            var saved = Services.PackagePasswordStore.Get();
            var ok = !string.IsNullOrEmpty(saved) &&
                     Services.ConfigPackageService.TryRead(path, saved, out payload, out error);

            if (!ok)
            {
                var prompt = PromptSecret("导入配置包", "请输入导出时设置的口令：");
                if (prompt == null) return;
                if (!Services.ConfigPackageService.TryRead(path, prompt, out payload, out error))
                {
                    _app.WriteDiagnosticLog($"[Package] 读取失败: {error}");
                    await SendToast("打开失败：" + error, "error");
                    return;
                }
                Services.PackagePasswordStore.Set(prompt);
            }

            var preview = Services.ConfigPackageService.BuildPreview(payload, _app.Config);
            _pendingPackage = preview;

            var hasExisting = _app.Config.Accounts.Any(a => !a.IsPlaceholder);
            var changedAccounts = preview.Accounts.Where(a => a.Kind != "unchanged").ToList();

            // 没有已有配置：直接确认；有配置：交给界面逐项勾选
            if (!hasExisting)
            {
                if (!Dialogs.Confirm(this, "加载配置包",
                        $"当前没有任何账号，是否加载这个配置包？\n\n" +
                        $"包含 {payload.Accounts.Count} 个账号、{preview.Settings.Count} 项与默认不同的设置" +
                        $"（来自 v{(string.IsNullOrWhiteSpace(preview.PackageVersion) ? "未知" : preview.PackageVersion)}，" +
                        $"导出于 {preview.CreatedAt}）。"))
                {
                    _pendingPackage = null;
                    return;
                }
                await ApplyPackageImportAsync(payload.Accounts.Select(a => a.Username).ToArray(),
                    preview.Settings.Select(s => s.Key).ToArray());
                return;
            }

            await SendToJs(new
            {
                type = "package-preview",
                accounts = preview.Accounts.Select(a => new { kind = a.Kind, username = a.Username, displayName = a.DisplayName, summary = a.Summary }).ToList(),
                settings = preview.Settings.Select(s => new { key = s.Key, label = s.Label, current = s.Current, incoming = s.Incoming }).ToList(),
                packageVersion = preview.PackageVersion,
                createdAt = preview.CreatedAt,
                changedAccounts = changedAccounts.Count
            });
        }

        /// <summary>界面勾选完成后调用：只应用选中的账号与设置。</summary>
        private async Task ApplyPackageImportAsync(string[] usernames, string[] settingKeys)
        {
            var preview = _pendingPackage;
            _pendingPackage = null;
            if (preview == null) { await SendToast("导入会话已失效，请重新选择文件", "warn"); return; }

            var result = Services.ConfigPackageService.Apply(preview, _app.Config, usernames, settingKeys);
            _app.SaveConfig("import-package");

            var summary = $"已应用：新增 {result.Added} 个账号，更新 {result.Updated} 个，设置 {result.Settings} 项";
            _app.WriteDiagnosticLog($"[Package] 应用完成：{summary}" +
                                   (result.NeedPassword.Count > 0 ? $"; 需重录密码={string.Join(",", result.NeedPassword)}" : "") +
                                   (result.NeedLogin.Count > 0 ? $"; 需扫码或补密码={string.Join(",", result.NeedLogin)}" : ""));

            await RefreshAccountList();
            await SendSettings();

            // 导入只搬账号元数据和密码，搬不动本机的凭据环境：
            // 扫码登录的会话文件（DPAPI）不会进包，主密钥坏掉时密码也存不进去。
            // 这两种情况以前都是静默的，用户只看到"导入后全部登录失败"，所以现在必须说清楚。
            var todo = new List<string>();
            if (result.NeedPassword.Count > 0)
                todo.Add($"{result.NeedPassword.Count} 个账号的密码需要重新录入（{string.Join("、", result.NeedPassword.Take(3))}"
                         + (result.NeedPassword.Count > 3 ? " 等" : "") + "）");
            if (result.NeedLogin.Count > 0)
                todo.Add($"{result.NeedLogin.Count} 个账号需要重新扫码或补密码（{string.Join("、", result.NeedLogin.Take(3))}"
                         + (result.NeedLogin.Count > 3 ? " 等" : "") + "）");

            if (todo.Count > 0)
            {
                await SendToast(summary + "。但 " + string.Join("；", todo), "warn");
                return;
            }

            await SendToast(summary, "ok");
        }
    }
}
