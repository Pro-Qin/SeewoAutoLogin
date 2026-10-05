using Microsoft.Web.WebView2.Core;
using SeewoAutoLogin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        #region Handlers

        private async Task HandleLogin(JsonElement root)
        {
            var username = root.GetProperty("username").GetString() ?? "";
            var password = root.GetProperty("password").GetString() ?? "";
            var displayName = root.GetProperty("displayName").GetString() ?? "";
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            { await SendToJs(new { type = "login-status", text = "请输入账号和密码" }); return; }

            _passwordLoginCancellation?.Cancel(); _passwordLoginCancellation?.Dispose();
            _passwordLoginCancellation = new CancellationTokenSource();
            var ct = _passwordLoginCancellation;
            try
            {
                await SendToJs(new { type = "login-status", text = "正在验证..." });
                var svc = new SeewoAuthService();
                // 记一条开始日志：出问题时才能区分「用户没点过」和「点了但失败了」
                _app?.WriteDiagnosticLog($"[Login] 开始密码登录: user={username}");
                var result = await svc.LoginAsync(username, password, ct.Token);
                if (result.Success)
                {
                    var acct = new SeewoAccount
                    {
                        DisplayName = string.IsNullOrEmpty(displayName) ? (result.UserInfo?.NickName ?? username) : displayName,
                        Username = username, Password = password, UserInfo = result.UserInfo
                    };
                    _app.AddAccount(acct);
                    await SendToJs(new { type = "login-status", text = "登录成功，已保存" });
                    await Task.Delay(600);
                    await RefreshAccountList();
                }
                else
                {
                    // 失败原因只回给界面的话，日志里什么都查不到（密码登录不像扫码那样有详细 HTTP 日志）
                    _app?.WriteDiagnosticLog($"[Login] 密码登录失败: kind={result.FailureKind}; message={result.ErrorMessage}");
                    await SendToJs(new { type = "login-status", text = $"登录失败: {result.ErrorMessage}" });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Login] 密码登录异常: {ex}");
                await SendToJs(new { type = "login-status", text = $"登录失败: {ex.Message}" });
            }
            finally { if (ReferenceEquals(_passwordLoginCancellation, ct)) { _passwordLoginCancellation.Dispose(); _passwordLoginCancellation = null; } }
        }

        private async void HandleDeleteAccount(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            if (MessageBox.Show($"确定删除 \"{acct.DisplayName ?? acct.Username}\" 吗？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _app.RemoveAccount(id);
            await RefreshAccountList();
        }

        private async void HandleSetActive(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            if (!string.IsNullOrEmpty(id)) { _app.SwitchActiveAccount(id); await RefreshAccountList(); }
        }

        private async void HandleMove(JsonElement root, int dir)
        {
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts; var idx = list.FindIndex(a => a.Id == id);
            if (idx < 0) return;
            var ni = idx + dir;
            if (ni < 0 || ni >= list.Count) return;
            var item = list[idx]; list.RemoveAt(idx); list.Insert(ni, item);
            _app.SaveConfig(); await RefreshAccountList();
        }

        private async void HandleEditTags(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            var tags = root.GetProperty("tags").GetString() ?? "";
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            acct.Tags = tags.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
            _app.SaveConfig(); await RefreshAccountList();
        }

        /// <summary>
        /// 修改账号备注。WebView2 不支持 window.prompt（点了没有任何反应，是静默失败），
        /// 所以凡是需要输入文字的地方都走这里的原生输入框。
        /// </summary>
        private async Task HandleRenameAccountDialogAsync(JsonElement root)
        {
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;

            var dialog = new TextInputDialog("修改备注", "给这个账号起个容易认的名字：", acct.DisplayName ?? "") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var name = (dialog.InputText ?? "").Trim();
            if (name.Length == 0) return;
            acct.DisplayName = name;
            _app.SaveConfig();
            await RefreshAccountList();
        }

        /// <summary>添加演示用的占位账号（同样不能用 window.prompt）</summary>
        private async Task HandleAddFakeAccountDialogAsync()
        {
            var dialog = new TextInputDialog("添加假账号", "名称（仅用于演示界面，不会真实登录）：",
                "测试用户" + Random.Shared.Next(1, 100)) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var name = (dialog.InputText ?? "").Trim();
            if (name.Length == 0) return;

            var fakeId = "FAKE_" + Guid.NewGuid().ToString("N")[..6];
            _app.Config.Accounts.Add(new SeewoAccount
            {
                Id = fakeId,
                DisplayName = name,
                Username = $"fake_{fakeId.ToLowerInvariant()}",
                Password = "fake_password_that_will_fail",
                IsPlaceholder = true,
            });
            _app.SaveConfig();
            await RefreshAccountList();
            await SendToJs(new { type = "login-status", text = "已添加假账号: " + name });
        }

        private async void HandleEditDisplayName(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            var name = root.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(name)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            acct.DisplayName = name.Trim(); _app.SaveConfig(); await RefreshAccountList();
        }

        private async void HandleSetPassword(JsonElement root)
        {
            var pw = root.GetProperty("password").GetString() ?? "";
            if (string.IsNullOrEmpty(pw)) return;
            // PBKDF2 + 加盐，哈希值本身再用 DPAPI 包裹后落盘
            _app.SetPluginPassword(pw);
            await SendToJs(new { type = "settings", passwordSet = true });
        }

        private async void HandleClearPassword()
        {
            _app.SetPluginPassword("");
            await SendToJs(new { type = "settings", passwordSet = false });
        }

        private async void HandleUnlock(JsonElement root)
        {
            var pw = root.GetProperty("password").GetString() ?? "";
            if (string.IsNullOrEmpty(pw)) { await SendToJs(new { type = "unlock-status", text = "请输入密码" }); return; }

            if (Services.PasswordService.IsLockedOut(out var seconds))
            {
                await SendToJs(new { type = "unlock-status", text = $"错误次数过多，请 {seconds} 秒后再试" });
                return;
            }

            var ok = _app.Config.UsePluginPassword && !string.IsNullOrEmpty(_app.Config.PluginPasswordHash)
                && _app.VerifyPluginPassword(pw);
            if (ok) { _unlocked = true; await SendToJs(new { type = "unlock-success" }); await SendSettings(); }
            else await SendToJs(new { type = "unlock-status", text = "密码错误" });
        }

        private void HandleUpdateSetting(JsonElement root)
        {
            var key = root.GetProperty("key").GetString();
            var val = root.GetProperty("value");
            switch (key)
            {
                case "usePluginPassword": _app.Config.UsePluginPassword = val.GetBoolean(); break;
                case "userListRotationEnabled": _app.Config.UserListRotationEnabled = val.GetBoolean(); break;
                case "userListRotationGroupSize": _app.Config.UserListRotationGroupSize = SeewoUserListRotationService.NormalizeGroupSize(val.GetInt32()); break;
                case "minimizeToTray": _app.Config.MinimizeToTray = val.GetBoolean(); break;
                case "startMinimized": _app.Config.StartMinimized = val.GetBoolean(); break;
                case "restoreHostsOnExit": _app.Config.RestoreHostsOnExit = val.GetBoolean(); break;
                case "autoRepairEnabled": _app.Config.AutoRepairEnabled = val.GetBoolean(); break;
                case "autoShowOverlay": _app.Config.AutoShowOverlay = val.GetBoolean(); break;
                case "autoCheckUpdate": _app.Config.AutoCheckUpdate = val.GetBoolean(); break;
                case "autoInstallAfterDownload":
                    _app.Config.AutoInstallAfterDownload = val.GetBoolean();
                    _app.WriteDiagnosticLog("[Update] 下载完成后自动安装：" + (_app.Config.AutoInstallAfterDownload ? "已开启" : "已关闭"));
                    break;
                case "autoStart":
                    {
                        // 与欢迎界面共用同一份状态：优先创建最高权限计划任务（开机免 UAC），失败回退注册表启动项
                        var enabled = val.GetBoolean();
                        _app.Config.AutoStartEnabled = enabled;
                        if (enabled)
                        {
                            var ok = AutoStartService.Enable(out var error, out var mode);
                            _app.WriteDiagnosticLog($"[AutoStart] 启用自启: ok={ok}; mode={mode}; {error}");
                            if (!ok || !string.IsNullOrEmpty(error)) _app.TrayIcon?.SetStatusText(error);
                            if (!ok)
                            {
                                // 计划任务需要管理员权限：自动询问是否提权重启（用户确认后才重启）
                                if (!AutoStartService.IsAdministrator())
                                {
                                    var choice = MessageBox.Show(
                                        "创建「最高权限计划任务」需要管理员权限，当前以普通权限运行。\n\n" +
                                        "是否现在以管理员身份重新启动本程序？（重启后开机不再需要授权）\n\n" +
                                        "  · 是   → 提权重启并重新尝试\n" +
                                        "  · 否   → 暂时保持当前设置",
                                        "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);
                                    if (choice == MessageBoxResult.Yes)
                                    {
                                        _app.Config.AutoStartEnabled = true;
                                        _app.SaveConfig();
                                        _app.RestartApp();
                                        return;
                                    }
                                }
                                else
                                {
                                    _app.NotifyError("开机自启未开启", "写入开机自启失败：" + error);
                                }
                            }
                        }
                        else if (!AutoStartService.Disable(out var offError))
                        {
                            _app.WriteDiagnosticLog($"[AutoStart] 停用自启失败: {offError}");
                        }
                        break;
                    }
            }
            _app.SaveConfig();
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

        /// <summary>批量导入账号（每行 账号,密码[,备注]）</summary>
        private async Task HandleBatchImportAsync(JsonElement root)
        {
            var text = root.TryGetProperty("text", out var element) ? element.GetString() : "";
            var (added, failed, messages) = _app.BatchImport(text);
            await SendToJs(new { type = "batch-import-result", added, failed, messages });
            await RefreshAccountList();
        }

        /// <summary>
        /// CSV 批量导入：选择 .csv 文件 → 解析（UTF-8 / GBK，RFC4180 引号规则）→ 新增或更新账号。
        /// 与粘贴导入的区别：用户名已存在时更新密码/备注，而不是当成失败跳过。
        /// </summary>
        private async Task HandleCsvImportAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 CSV 文件",
                Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                await SendToJs(new { type = "csv-imported", cancelled = true, added = 0, updated = 0, failed = 0, messages = Array.Empty<string>() });
                return;
            }

            var added = 0;
            var updated = 0;
            var failed = 0;
            var rows = new List<List<string>>();
            var headerSkipped = false;
            var messages = new List<string>();

            try
            {
                rows = ParseCsv(ReadCsvText(dialog.FileName));

                for (var i = 0; i < rows.Count; i++)
                {
                    var cells = rows[i];
                    if (cells.All(c => string.IsNullOrWhiteSpace(c))) continue;
                    if (i == 0 && IsCsvHeaderRow(cells)) { headerSkipped = true; continue; }

                    var lineNo = i + 1;
                    var username = cells.Count > 0 ? cells[0].Trim() : "";
                    var password = cells.Count > 1 ? cells[1].Trim() : "";
                    var note = cells.Count > 2 ? cells[2].Trim() : "";

                    if (username.Length == 0 || password.Length == 0)
                    {
                        failed++;
                        AddCsvMessage(messages, $"第 {lineNo} 行：账号或密码为空，已跳过");
                        continue;
                    }

                    try
                    {
                        var existing = _app.Config.Accounts.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Password = Services.SecureStore.IsEncrypted(password) ? password : Services.SecureStore.Encrypt(password);
                            // 备注留空时保留原有显示名，避免一次导入把用户自己改过的备注清掉
                            if (note.Length > 0) existing.DisplayName = note;
                            updated++;
                        }
                        else
                        {
                            var account = new SeewoAccount
                            {
                                Username = username,
                                Password = Services.SecureStore.IsEncrypted(password) ? password : Services.SecureStore.Encrypt(password),
                                DisplayName = note.Length > 0 ? note : username
                            };
                            _app.Config.Accounts.Add(account);
                            if (_app.Config.Accounts.Count == 1) _app.Config.ActiveAccountId = account.Id;
                            added++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AddCsvMessage(messages, $"第 {lineNo} 行：导入失败（{ex.Message}）");
                    }
                }

                _app.WriteDiagnosticLog($"[CsvImport] 解析 {rows.Count} 行：新增 {added}，更新 {updated}，失败 {failed}{(headerSkipped ? "，已跳过表头" : "")}");
            }
            catch (Exception ex)
            {
                // 解析中断时不要抛给 WebView 消息循环：把原因带回前端，让用户能自己改文件重试
                _app.WriteDiagnosticLog($"[CsvImport] 导入异常: {ex.GetType().Name} - {ex.Message}");
                failed++;
                AddCsvMessage(messages, "读取或解析文件失败：" + ex.Message);
            }
            finally
            {
                // 中途失败时已计入的账号也已写进内存配置，这里统一落盘，避免内存与磁盘不一致
                if (added > 0 || updated > 0)
                {
                    try { _app.SaveConfig(); }
                    catch (Exception ex) { _app.WriteDiagnosticLog($"[CsvImport] 保存配置失败: {ex.Message}"); }
                }
            }

            await SendToJs(new { type = "csv-imported", cancelled = false, added, updated, failed, messages });
            await RefreshAccountList();
        }

        /// <summary>失败明细最多回传这么多条，避免上千行的文件把消息体撑爆（前端只展示前 5 条）</summary>
        private const int CsvImportMaxMessages = 20;

        private static void AddCsvMessage(List<string> messages, string message)
        {
            if (messages.Count < CsvImportMaxMessages) messages.Add(message);
            else if (messages.Count == CsvImportMaxMessages) messages.Add("（失败明细过多，其余条目已省略）");
        }

        /// <summary>
        /// 读 CSV 文本：按 BOM 判定 UTF-8 / UTF-16，无 BOM 时先按 UTF-8 严格解码，
        /// 失败再回退 GBK —— Excel「另存为 CSV」在中文 Windows 上默认就是 GBK。
        /// </summary>
        private static string ReadCsvText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return "";

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    // GBK(936) 属于代码页编码，.NET Core 需要先注册提供程序
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(936).GetString(bytes);
                }
                catch
                {
                    return Encoding.UTF8.GetString(bytes);
                }
            }
        }

        /// <summary>
        /// RFC4180 风格 CSV 解析：支持引号包裹的字段、字段内逗号与换行、双写引号（""）转义，
        /// 兼容 CRLF / LF / CR 换行。不丢空行，由调用方决定跳过。
        /// </summary>
        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrEmpty(text)) return rows;
            if (text[0] == '\uFEFF') text = text.Substring(1);

            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // 引号内的 "" 表示一个字面引号
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                    continue;
                }

                if (ch == '"' && field.Length == 0) { inQuotes = true; continue; }

                if (ch == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    continue;
                }

                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    continue;
                }

                field.Append(ch);
            }

            // 文件末尾没有换行时补上最后一行；末尾正好有换行则不会多出一行空记录
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>首列可能出现的表头字样（账号,密码,备注 这类列名）</summary>
        private static readonly string[] CsvHeaderUserColumns =
        {
            "账号", "帐号", "用户名", "用户", "登录名", "手机号", "手机", "姓名",
            "user", "username", "account", "login", "mobile", "phone"
        };

        /// <summary>首列是「序号/no/id」这类占位列时，再看第二列是不是账号列</summary>
        private static readonly string[] CsvHeaderIndexColumns = { "序号", "编号", "no", "id", "#", "index" };

        /// <summary>
        /// 首行是否为表头。只做精确匹配（而不是 Contains），否则「13800138000」这种
        /// 真实账号会被当成表头整行丢掉。
        /// </summary>
        private static bool IsCsvHeaderRow(List<string> cells)
        {
            if (cells == null || cells.Count == 0) return false;
            var first = cells[0].Trim().ToLowerInvariant();
            if (first.Length == 0) return false;
            if (CsvHeaderUserColumns.Contains(first)) return true;
            if (CsvHeaderIndexColumns.Contains(first) && cells.Count > 1)
                return CsvHeaderUserColumns.Contains(cells[1].Trim().ToLowerInvariant());
            return false;
        }

        /// <summary>
        /// 编辑标签：用原生输入框而不是前端 prompt —— WebView2 里 window.prompt 不被支持，
        /// 用 prompt 会出现「点了『标签』毫无反应」的静默失败。
        /// </summary>
        private async void HandleEditTagsDialog(JsonElement root)
        {
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;

            var current = acct.Tags != null && acct.Tags.Count > 0 ? string.Join(", ", acct.Tags) : "";
            var dialog = new TextInputDialog("编辑标签", "标签（用逗号分隔，留空表示清除）：", current) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            acct.Tags = ParseTagList(dialog.InputText);
            _app.SaveConfig();
            await RefreshAccountList();
            await SendToJs(new
            {
                type = "toast",
                text = acct.Tags.Count > 0 ? $"已更新标签：{string.Join(", ", acct.Tags)}" : "已清除该账号的标签",
                level = "ok"
            });
        }

        /// <summary>标签解析：兼容中英文逗号与分号，去空白、去重复（忽略大小写）</summary>
        private static List<string> ParseTagList(string text) => (text ?? "")
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>列出配置备份</summary>
        private async Task SendBackups()
        {
            var items = _app.ListConfigBackups().Select(b => new
            {
                name = b.Name,
                time = b.Time.ToString("yyyy-MM-dd HH:mm"),
                accounts = b.Accounts
            }).ToList();
            await SendToJs(new { type = "backups", items });
        }

        /// <summary>从备份恢复配置</summary>
        private async Task HandleRestoreBackupAsync(JsonElement root)
        {
            var name = root.TryGetProperty("name", out var element) ? element.GetString() : "";
            if (!_app.RestoreConfigBackup(name, out var error))
            {
                await SendToJs(new { type = "toast", text = "恢复失败：" + error, level = "error" });
                return;
            }
            await SendToJs(new { type = "toast", text = $"已从备份恢复：{name}", level = "info" });
            await SendBackups();
            await RefreshAccountList();
        }

        /// <summary>教程结束：记录状态，避免每次启动都自动播放</summary>
        private void HandleTourDone(JsonElement root)
        {
            var completed = root.TryGetProperty("completed", out var element) && element.ValueKind == JsonValueKind.True;
            _app.Config.PendingTour = false;
            if (completed) _app.Config.TourCompleted = true;
            _app.SaveConfig();
            _app.WriteDiagnosticLog($"[Tour] 教程结束; completed={completed}");
            _ = SendToJs(new { type = "toast", text = "教程结束，随时可在「设置 → 帮助与维护」里重看", level = "ok" });
        }

        /// <summary>恢复出厂设置：前端已确认一次，这里再确认一次并说明后果</summary>
        private void HandleFactoryReset()
        {
            var choice = MessageBox.Show(
                "恢复出厂设置会：\n" +
                "  · 删除全部账号（含扫码凭据）\n" +
                "  · 清空所有设置与配置备份\n" +
                "  · 回到首次安装的引导流程\n\n" +
                "此操作不可撤销，确定继续吗？",
                "恢复出厂设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (choice != MessageBoxResult.Yes)
            {
                _ = SendToJs(new { type = "factory-reset-cancelled" });
                return;
            }

            _app.FactoryReset();
            _ = SendToJs(new { type = "factory-reset-done" });
            _app.WriteDiagnosticLog("[Reset] 即将自动重启，回到首次使用流程");

            // 稍等一下让界面把提示画出来，然后直接重启（不必让用户自己再点一次）
            var restartTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            restartTimer.Tick += (_, _) =>
            {
                restartTimer.Stop();
                _app.RestartApp();
            };
            restartTimer.Start();
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

        /// <summary>下发切换口令的启用状态</summary>
        private async Task SendSwitchPinStatusAsync()
        {
            try { await SendToJs(new { type = "switch-pin", enabled = _app.SwitchPin.IsEnabled }); }
            catch { }
        }

        private async Task SetSwitchPinAsync(JsonElement root)
        {
            // 已经设过口令时，改口令必须先验证旧口令 —— 否则任何人打开设置就能把口令换掉，功能白做
            if (_app.SwitchPin.IsEnabled && !RequireSwitchPin()) return;

            var pin = root.TryGetProperty("pin", out var e) ? e.GetString() : null;
            var (ok, error) = _app.SwitchPin.Set(pin ?? "");
            if (!ok) { await SendToast("设置失败：" + error, "error"); return; }
            _app.WriteDiagnosticLog("[SwitchPin] 已设置切换口令");
            await SendToast("切换口令已设置：以后切换账号需要先输入", "ok");
            await SendSwitchPinStatusAsync();
        }

        private async Task ClearSwitchPinAsync()
        {
            // 清除口令同样要先验证：口令的意义就是拦住「随手切换账号」的人，
            // 如果连清除都不设防，点一下「清除口令」就绕过去了。
            if (_app.SwitchPin.IsEnabled && !RequireSwitchPin())
            {
                // 验证失败时回填开关状态，避免界面停在「已取消勾选」的假象
                await SendSwitchPinStatusAsync();
                return;
            }

            _app.SwitchPin.Clear();
            _app.WriteDiagnosticLog("[SwitchPin] 已清除切换口令");
            await SendToast("已清除切换口令，切换账号不再需要验证", "ok");
            await SendSwitchPinStatusAsync();
        }

        /// <summary>
        /// 需要口令的账号切换操作前调用。未设置口令、或验证通过时返回 true。
        /// 面向一台班班设备多位老师共用的场景，避免随手点开别人的账号。
        /// </summary>
        private bool RequireSwitchPin()
        {
            if (!_app.SwitchPin.IsEnabled) return true;

            var dialog = new TextInputDialog("输入切换口令", "切换账号需要口令：", "") { Owner = this };
            if (dialog.ShowDialog() != true) return false;

            if (_app.SwitchPin.Verify(dialog.InputText ?? "")) return true;

            _app.WriteDiagnosticLog("[SwitchPin] 切换口令校验失败");
            _ = SendToast("口令不正确", "error");
            return false;
        }

        /// <summary>
        /// 下发凭据有效期观测结论。样本来自后台每次续期的实测记录，
        /// 样本不足时说明"暂无结论"，不编造。
        /// </summary>
        private async Task SendCredentialLifetimeAsync()
        {
            string text;
            try { text = _app.CredentialLifetimeDescription ?? ""; }
            catch { text = ""; }
            await SendToJs(new { type = "credential-lifetime", text });
        }

        /// <summary>弹一个输入框要口令（明文可读，属于一次性输入，够用）</summary>
        private string PromptSecret(string title, string hint)
        {
            var dialog = new TextInputDialog(title, hint, "") { Owner = this };
            return dialog.ShowDialog() == true ? (dialog.InputText ?? "").Trim() : null;
        }

        private Task SendToast(string text, string level)
            => SendToJs(new { type = "toast", text, level });

        /// <summary>
        /// 导出账号（含密码）。本机密码是 DPAPI 加密的、换台电脑解不开，
        /// 所以这里让用户设一个导出密码重新加密成可搬走的文件。
        /// </summary>
        private async Task HandleExportAccountsAsync()
        {
            var accounts = _app.Config.Accounts.Where(a => !a.IsPlaceholder).ToList();
            if (accounts.Count == 0) { await SendToast("没有可导出的账号", "warn"); return; }

            var pin = PromptSecret("导出账号", $"将为 {accounts.Count} 个账号设置一个导出密码（至少 6 位，导入时需要）：");
            if (pin == null) return;
            if (pin.Length < 6) { await SendToast("导出密码至少 6 位", "error"); return; }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "账号备份 (*.seewoacct)|*.seewoacct",
                FileName = $"SeewoAutoLogin-账号-{DateTime.Now:yyyyMMdd}.seewoacct",
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                Services.AccountPortableExport.Export(dialog.FileName, accounts, pin);
                _app.WriteDiagnosticLog($"[Accounts] 已导出 {accounts.Count} 个账号到 {dialog.FileName}");
                await SendToast($"已导出 {accounts.Count} 个账号（用你设的密码加密）", "ok");
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Accounts] 导出失败: {ex.Message}");
                await SendToast("导出失败：" + ex.Message, "error");
            }
        }

        /// <summary>从加密文件导入账号：用户名相同的更新，其余新增</summary>
        private async Task HandleImportAccountsAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "账号备份 (*.seewoacct)|*.seewoacct|所有文件 (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;

            var pin = PromptSecret("导入账号", "请输入导出时设置的密码：");
            if (pin == null) return;

            var (imported, error) = Services.AccountPortableExport.Import(dialog.FileName, pin);
            if (!string.IsNullOrEmpty(error))
            {
                _app.WriteDiagnosticLog($"[Accounts] 导入失败: {error}");
                await SendToast("导入失败：" + error, "error");
                return;
            }

            var added = 0;
            var updated = 0;
            foreach (var incoming in imported)
            {
                if (string.IsNullOrWhiteSpace(incoming.Username)) continue;
                var existing = _app.Config.Accounts.FirstOrDefault(a =>
                    string.Equals(a.Username, incoming.Username, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Password = incoming.Password;
                    existing.Tags = incoming.Tags;
                    if (!string.IsNullOrWhiteSpace(incoming.DisplayName)) existing.DisplayName = incoming.DisplayName;
                    updated++;
                }
                else
                {
                    _app.Config.Accounts.Add(incoming);
                    added++;
                }
            }

            _app.SaveConfig();
            _app.WriteDiagnosticLog($"[Accounts] 导入完成：新增 {added}，更新 {updated}");
            await RefreshAccountList();
            await SendToast($"导入完成：新增 {added} 个，更新 {updated} 个", "ok");
        }

        private void HandleExportConfig()
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = "导出配置", Filter = "配置文件 (*.json)|*.json", FileName = $"SeewoAutoLogin_config_{DateTime.Now:yyyyMMdd}.json" };
            if (d.ShowDialog() == true) { _app.ExportConfig(d.FileName); _app.TrayIcon?.SetStatusText("配置已导出"); }
        }

        private async Task HandleAddFakeAccount(JsonElement root)
        {
            var displayName = root.GetProperty("displayName").GetString() ?? "测试用户";
            var fakeId = "FAKE_" + Guid.NewGuid().ToString("N")[..6];
            var fakeAccount = new SeewoAccount
            {
                Id = fakeId,
                DisplayName = displayName,
                Username = $"fake_{fakeId.ToLower()}",
                Password = "fake_password_that_will_fail",
                // 标记为占位账号：后续的保活与巡检会跳过它
                IsPlaceholder = true
            };
            _app.Config.Accounts.Add(fakeAccount);
            _app.SaveConfig();
            await RefreshAccountList();
            await SendToJs(new { type = "login-status", text = $"已添加假账号: {displayName}" });
        }

        private async void HandleMoveToActive(JsonElement root)
        {
            if (!RequireSwitchPin()) return;
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts;
            var idx = list.FindIndex(a => a.Id == id);
            if (idx < PluginConfig.MaxVisibleAccounts) return; // 已经在生效区
            var acct = list[idx];
            list.RemoveAt(idx);
            // 插入到生效区最后一个
            list.Insert(PluginConfig.MaxVisibleAccounts - 1, acct);
            _app.SaveConfig();
            await RefreshAccountList();
        }

        private async void HandleMoveToInactive(JsonElement root)
        {
            if (!RequireSwitchPin()) return;
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts;
            var idx = list.FindIndex(a => a.Id == id);
            if (idx < 0 || idx >= PluginConfig.MaxVisibleAccounts) return; // 不在生效区
            var acct = list[idx];
            list.RemoveAt(idx);
            // 插入到生效区边界，生效区减一，不自动补位
            list.Insert(Math.Min(PluginConfig.MaxVisibleAccounts, list.Count), acct);
            _app.SaveConfig();
            await RefreshAccountList();
        }

        private void HandleImportConfig()
        {
            if (MessageBox.Show("导入配置将覆盖当前所有设置，确定？", "导入", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var d = new Microsoft.Win32.OpenFileDialog { Title = "导入配置", Filter = "配置文件 (*.json)|*.json" };
            if (d.ShowDialog() == true && _app.ImportConfig(d.FileName)) { _app.TrayIcon?.SetStatusText("配置已导入"); _app.RestartApp(); }
        }

        #endregion
    }
}
