using System.Windows;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>
        /// 拖拽后的新顺序：前端把两栏的最终顺序整份发过来，这里照单全收。
        /// 生效区仍然是「列表前 N 个」，所以拖到哪一栏、第几位，就是它最终的位置。
        /// </summary>
        private async Task HandleReorderAccountsAsync(JsonElement root)
        {
            try
            {
                var ids = root.Strings("ids");
                if (ids.Count == 0) return;

                // 被标签筛掉、没出现在这次拖拽里的账号，由 ApplyOrder 保持原顺序接在后面
                AccountOrdering.ApplyOrder(_app.Config.Accounts, ids);
                _app.SaveConfig();
                await RefreshAccountList();
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Accounts] 重排失败: {ex.GetType().Name} - {ex.Message}");
            }
        }

        /// <summary>界面上的「续期」/「全部续期」：立刻用保存的凭据重试，并把结果说清楚</summary>
        private async Task HandleRenewAccountsAsync(JsonElement root)
        {
            try
            {
                var accountId = root.Str("id");
                var single = !string.IsNullOrWhiteSpace(accountId);

                await SendToJs(new { type = "renew-status", running = true, single });
                var (refreshed, failed) = await _app.RenewAccountsAsync(accountId).ConfigureAwait(true);

                if (refreshed < 0)
                {
                    await SendToJs(new { type = "renew-status", running = false, text = "后台正在自动巡检，请稍候再试" });
                    return;
                }

                await RefreshAccountList();
                var text = failed == 0
                    ? (refreshed > 0 ? $"续期完成：成功 {refreshed} 个" : "没有需要续期的账号")
                    : $"续期完成：成功 {refreshed} 个，失败 {failed} 个（详情见「登录记录」或账号卡片上的说明）";
                await SendToJs(new { type = "renew-status", running = false, text });
                await SendToast(text, failed > 0 ? "warn" : "ok");
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[KeepAlive] 手动续期异常: {ex.GetType().Name} - {ex.Message}");
                await SendToJs(new { type = "renew-status", running = false, text = "续期失败：" + ex.Message });
            }
        }

        private async Task HandleDeleteAccountAsync(JsonElement root)
        {
            var id = root.Str("id");
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            if (!Dialogs.ConfirmDanger(this, "删除", $"确定删除 \"{acct.DisplayName ?? acct.Username}\" 吗？")) return;
            _app.RemoveAccount(id);
            await RefreshAccountList();
        }

        private async Task HandleMoveAsync(JsonElement root, int dir)
        {
            var id = root.Str("id");
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts; var idx = list.FindIndex(a => a.Id == id);
            if (idx < 0) return;
            var ni = idx + dir;
            if (ni < 0 || ni >= list.Count) return;
            var item = list[idx]; list.RemoveAt(idx); list.Insert(ni, item);
            _app.SaveConfig(); await RefreshAccountList();
        }

        private async Task HandleEditTagsAsync(JsonElement root)
        {
            var id = root.Str("id");
            var tags = root.Str("tags");
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            acct.Tags = AccountOrdering.ParseTags(tags);
            _app.SaveConfig(); await RefreshAccountList();
        }

        /// <summary>
        /// 修改账号备注。WebView2 不支持 window.prompt（点了没有任何反应，是静默失败），
        /// 所以凡是需要输入文字的地方都走这里的原生输入框。
        /// </summary>
        private async Task HandleRenameAccountDialogAsync(JsonElement root)
        {
            var id = root.Str("id", null);
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

        private async Task HandleEditDisplayNameAsync(JsonElement root)
        {
            var id = root.Str("id");
            var name = root.Str("name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(name)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            acct.DisplayName = name.Trim(); _app.SaveConfig(); await RefreshAccountList();
        }

        /// <summary>
        /// 编辑标签：用原生输入框而不是前端 prompt —— WebView2 里 window.prompt 不被支持，
        /// 用 prompt 会出现「点了『标签』毫无反应」的静默失败。
        /// </summary>
        private async Task HandleEditTagsDialogAsync(JsonElement root)
        {
            var id = root.Str("id", null);
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;

            var current = acct.Tags != null && acct.Tags.Count > 0 ? string.Join(", ", acct.Tags) : "";
            var dialog = new TextInputDialog("编辑标签", "标签（用逗号分隔，留空表示清除）：", current) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            acct.Tags = AccountOrdering.ParseTags(dialog.InputText);
            _app.SaveConfig();
            await RefreshAccountList();
            await SendToJs(new
            {
                type = "toast",
                text = acct.Tags.Count > 0 ? $"已更新标签：{string.Join(", ", acct.Tags)}" : "已清除该账号的标签",
                level = "ok"
            });
        }

        private async Task HandleAddFakeAccount(JsonElement root)
        {
            var displayName = root.Str("displayName");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "测试用户";
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

        private async Task HandleMoveToActiveAsync(JsonElement root)
        {
            if (!RequireSwitchPin()) return;
            var id = root.Str("id");
            if (string.IsNullOrEmpty(id)) return;
            // 已经在生效区时不做变更（边界规则见 AccountOrdering）
            if (!AccountOrdering.MoveToActive(_app.Config.Accounts, id)) return;
            _app.SaveConfig();
            await RefreshAccountList();
        }

        private async Task HandleMoveToInactiveAsync(JsonElement root)
        {
            if (!RequireSwitchPin()) return;
            var id = root.Str("id");
            if (string.IsNullOrEmpty(id)) return;
            // 本来就不在生效区时不做变更（边界规则见 AccountOrdering）
            if (!AccountOrdering.MoveToInactive(_app.Config.Accounts, id)) return;
            _app.SaveConfig();
            await RefreshAccountList();
        }

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleReorderAccounts(JsonElement root) => RunCommand(() => HandleReorderAccountsAsync(root), "HandleReorderAccounts");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleDeleteAccount(JsonElement root) => RunCommand(() => HandleDeleteAccountAsync(root), "HandleDeleteAccount");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleMove(JsonElement root, int dir) => RunCommand(() => HandleMoveAsync(root, dir), "HandleMove");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleEditTags(JsonElement root) => RunCommand(() => HandleEditTagsAsync(root), "HandleEditTags");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleEditDisplayName(JsonElement root) => RunCommand(() => HandleEditDisplayNameAsync(root), "HandleEditDisplayName");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleEditTagsDialog(JsonElement root) => RunCommand(() => HandleEditTagsDialogAsync(root), "HandleEditTagsDialog");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleMoveToActive(JsonElement root) => RunCommand(() => HandleMoveToActiveAsync(root), "HandleMoveToActive");

        /// <summary>前端命令入口：异常与取消统一由 <see cref="RunCommand"/> 兜底。</summary>
        private void HandleMoveToInactive(JsonElement root) => RunCommand(() => HandleMoveToInactiveAsync(root), "HandleMoveToInactive");
    }
}
