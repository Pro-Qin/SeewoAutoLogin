namespace SeewoAutoLogin
{
    public partial class App
    {
        /// <summary>一次升级提醒的完整内容，来源是升级前落盘的那份记录。</summary>
        internal sealed class UpgradeNoticeData
        {
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public DateTimeOffset AtUtc { get; set; }
            public string Notes { get; set; } = "";
        }

        /// <summary>
        /// 取出「本次升级」要提示的内容。返回 null 表示不该提示。
        ///
        /// 三种情况都不提示：没有记录、已经提示过、当前版本还没到位（安装被安全软件拦下 / 被回滚 /
        /// 装到了别的目录）。最后一种最要紧 —— 否则会出现「说升到了 X、版本却还是旧」的自相矛盾提示。
        ///
        /// 主界面内的提醒与桌面提醒都走这里，所以两边永远不会同时弹：先到的那边走完就把标记消费掉了。
        /// </summary>
        /// <summary>
        /// 记录这次升级的起止版本、时间与更新内容，供新版本第一次打开界面时提示一次。
        ///
        /// 后台静默更新与手动点「下载新版本」都要记 —— 原先只有前者会写，
        /// 手动升级的人在新版本打开后永远看不到升级提醒。
        /// </summary>
        internal void RecordUpgradeNotice(UpdateInfo info)
        {
            try
            {
                if (info == null || string.IsNullOrWhiteSpace(info.Version)) return;

                _config.UpgradeNoticeFromVersion = CurrentAppVersion;
                _config.UpgradeNoticeToVersion = info.Version;
                _config.UpgradeNoticeAtUtc = DateTimeOffset.UtcNow;

                var notes = (info.Notes ?? "").Trim();
                _config.UpgradeNoticeNotes = notes.Length <= 4000 ? notes : notes.Substring(0, 4000) + "\n…";
                // 这是一次新的升级，允许再提示一次
                _config.UpgradeNoticeShown = false;
                WriteDiagnosticLog($"[Update] 已记录升级提醒：{CurrentAppVersion} → {info.Version}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Update] 记录升级提醒失败: {ex.Message}");
            }
        }

        /// <param name="markShown">true 表示顺带把「已提示」落盘，保证只提示一次。</param>
        internal UpgradeNoticeData TryConsumeUpgradeNotice(bool markShown)
        {
            try
            {
                var cfg = _config;
                if (cfg == null || cfg.UpgradeNoticeShown) return null;

                var to = UpdateChecker.NormalizeVersion(cfg.UpgradeNoticeToVersion);
                if (string.IsNullOrWhiteSpace(to)) return null;

                var current = CurrentAppVersion;
                if (UpdateChecker.CompareVersions(current, to) < 0)
                {
                    WriteDiagnosticLog($"[Update] 跳过升级提醒：当前 {current} 仍低于记录的 {to}");
                    return null;
                }

                var from = UpdateChecker.NormalizeVersion(cfg.UpgradeNoticeFromVersion);
                // 记录里的起始版本已不低于当前版本：这次对比失去意义（例如用户之后又手动装了更新的版本），
                // 只显示当前版本，不编造起止关系。
                if (!string.IsNullOrWhiteSpace(from) && UpdateChecker.CompareVersions(from, current) >= 0)
                    from = "";

                if (markShown)
                {
                    cfg.UpgradeNoticeShown = true;
                    SaveConfig();
                }

                return new UpgradeNoticeData
                {
                    From = from,
                    To = current,
                    AtUtc = cfg.UpgradeNoticeAtUtc ?? DateTimeOffset.UtcNow,
                    Notes = cfg.UpgradeNoticeNotes ?? ""
                };
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Update] 读取升级提醒失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 调试预览用：不校验「已提示」标记，优先用真实记录；没有记录时给一份明确标注为预览的内容，
        /// 不假装成一次真实的升级。
        /// </summary>
        internal UpgradeNoticeData BuildPreviewUpgradeNotice()
        {
            var cfg = _config;
            var current = CurrentAppVersion;

            var from = UpdateChecker.NormalizeVersion(cfg?.UpgradeNoticeFromVersion);
            if (!string.IsNullOrWhiteSpace(from) && UpdateChecker.CompareVersions(from, current) >= 0)
                from = "";

            var notes = cfg?.UpgradeNoticeNotes;
            if (string.IsNullOrWhiteSpace(notes))
                notes = "（调试预览）本次没有记录到更新内容。\n真实的后台自动更新完成之后，这里会显示该版本的改动清单。";

            return new UpgradeNoticeData
            {
                From = from,
                To = current,
                AtUtc = cfg?.UpgradeNoticeAtUtc ?? DateTimeOffset.UtcNow,
                Notes = notes
            };
        }

        /// <summary>
        /// 主界面不会自动打开时（托盘常驻的机器），用桌面提醒把「升级完成」说出来 ——
        /// 否则用户既看不到界面内的提醒，也不知道程序已经升过版。
        /// </summary>
        private async Task ShowDesktopUpgradeNoticeIfNeededAsync()
        {
            try
            {
                // 稍等片刻：让托盘图标、SSO 网关这些启动动作先落定，别和它们抢注意力
                await Task.Delay(2500);

                var data = TryConsumeUpgradeNotice(markShown: true);
                if (data == null) return;

                UpgradeToastWindow.ShowNotice(data);
                WriteDiagnosticLog(
                    $"[Update] 已弹出桌面升级提醒：{(string.IsNullOrWhiteSpace(data.From) ? "（无起始版本记录）" : data.From)} → {data.To}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Update] 桌面升级提醒失败: {ex.Message}");
            }
        }
    }
}
