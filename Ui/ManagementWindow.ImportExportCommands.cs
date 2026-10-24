using System.Windows;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>批量导入账号（每行 账号,密码[,备注]）</summary>
        private async Task HandleBatchImportAsync(JsonElement root)
        {
            var text = root.Str("text");
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
                rows = CsvParser.Parse(CsvParser.ReadText(dialog.FileName));

                var dataRowSeen = false;
                for (var i = 0; i < rows.Count; i++)
                {
                    var cells = rows[i];
                    if (cells.All(c => string.IsNullOrWhiteSpace(c))) continue;
                    // 按「这是不是第一条数据」判断，不看行号：文件开头是空行时表头会落到第 2 行，
                    // 用 i==0 判断就会把「账号,密码,备注」当成一条账号记录导进去。
                    if (!dataRowSeen && CsvParser.IsHeaderRow(cells)) { headerSkipped = true; continue; }
                    dataRowSeen = true;

                    var lineNo = i + 1;
                    var username = cells.Count > 0 ? cells[0].Trim() : "";
                    var password = cells.Count > 1 ? cells[1].Trim() : "";
                    var note = cells.Count > 2 ? cells[2].Trim() : "";

                    if (username.Length == 0 || password.Length == 0)
                    {
                        failed++;
                        CsvParser.AddMessage(messages, $"第 {lineNo} 行：账号或密码为空，已跳过");
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
                            // 覆盖密码要说出来：一次误导入就能把某个账号的密码换成 CSV 里的旧值，
                            // 而用户只看到一句「更新 N 个」。
                            CsvParser.AddMessage(messages, $"已更新：{existing.DisplayName ?? username}（密码被 CSV 里的值覆盖，备注为空白时保留原值）");
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
                        CsvParser.AddMessage(messages, $"第 {lineNo} 行：导入失败（{ex.Message}）");
                    }
                }

                _app.WriteDiagnosticLog($"[CsvImport] 解析 {rows.Count} 行：新增 {added}，更新 {updated}，失败 {failed}{(headerSkipped ? "，已跳过表头" : "")}");
            }
            catch (Exception ex)
            {
                // 解析中断时不要抛给 WebView 消息循环：把原因带回前端，让用户能自己改文件重试
                _app.WriteDiagnosticLog($"[CsvImport] 导入异常: {ex.GetType().Name} - {ex.Message}");
                failed++;
                CsvParser.AddMessage(messages, "读取或解析文件失败：" + ex.Message);
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


        /// <summary>列出配置备份</summary>
        private async Task SendBackups()
        {
            var items = _app.ListConfigBackups().Select(b => new
            {
                name = b.Name,
                time = b.Time.ToString("yyyy-MM-dd HH:mm"),
                accounts = b.Accounts,
                size = b.Size,
                reason = b.Reason ?? ""
            }).ToList();
            await SendToJs(new { type = "backups", items });
        }

        /// <summary>删除一份备份（清理用；保留策略之外的手动动作）</summary>
        private async Task HandleDeleteBackupAsync(JsonElement root)
        {
            var name = root.Str("name");
            if (string.IsNullOrEmpty(name)) return;
            if (_app.DeleteConfigBackup(name, out var error))
                await SendToast("已删除该备份", "ok");
            else
                await SendToast("删除失败：" + error, "error");
            await SendBackups();
        }

        /// <summary>从备份恢复配置</summary>
        private async Task HandleRestoreBackupAsync(JsonElement root)
        {
            var name = root.Str("name");
            if (!_app.RestoreConfigBackup(name, out var error))
            {
                await SendToJs(new { type = "toast", text = "恢复失败：" + error, level = "error" });
                return;
            }
            await SendToJs(new { type = "toast", text = $"已从备份恢复：{name}", level = "info" });
            await SendBackups();
            await RefreshAccountList();
        }

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
            if (d.ShowDialog() == true) { _app.ExportConfig(d.FileName); _app.TrayIcon?.Notify("配置已导出"); }
        }

        private void HandleImportConfig()
        {
            if (!Dialogs.ConfirmDanger(this, "导入", "导入配置将覆盖当前所有设置，确定？")) return;
            var d = new Microsoft.Win32.OpenFileDialog { Title = "导入配置", Filter = "配置文件 (*.json)|*.json" };
            if (d.ShowDialog() == true && _app.ImportConfig(d.FileName)) { _app.TrayIcon?.Notify("配置已导入"); _app.RestartApp(); }
        }
    }
}
