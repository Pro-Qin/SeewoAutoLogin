using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// ClassIsland 同款静默更新协调器：
    /// 启动后延迟检查，有新版本且开启自动安装时后台下载、校验、记录待安装状态，
    /// 拉起 Inno Setup 静默安装并请求主程序退出；新版本带 --updated 启动后收尾。
    /// 不依赖主界面是否打开。
    /// </summary>
    internal sealed class UpdateCoordinator
    {
        private readonly PluginConfig _config;
        private readonly Action<string> _log;
        private readonly Action _saveConfig;
        private readonly Action<string> _setTrayStatus;
        private readonly Action _requestExit;
        private readonly Action<Action> _invokeOnUi;
        private readonly Action<string, double?> _notifyUi;
        private readonly Func<bool> _isMainWindowOpen;
        private int _running;

        public UpdateCoordinator(
            PluginConfig config,
            Action<string> log,
            Action saveConfig,
            Action<string> setTrayStatus,
            Action requestExit,
            Action<Action> invokeOnUi,
            Action<string, double?> notifyUi = null,
            Func<bool> isMainWindowOpen = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? (_ => { });
            _saveConfig = saveConfig ?? (() => { });
            _setTrayStatus = setTrayStatus ?? (_ => { });
            _requestExit = requestExit ?? (() => { });
            _invokeOnUi = invokeOnUi ?? (action => action());
            _notifyUi = notifyUi;
            _isMainWindowOpen = isMainWindowOpen ?? (() => false);
        }

        /// <summary>后台静默更新是否正在运行。</summary>
        public bool IsRunning => _running > 0;

        /// <summary>
        /// 启动后延迟检查，有新版本且开启自动安装时执行后台下载与静默安装。
        /// 不依赖主界面；失败只写日志与托盘提示。
        /// </summary>
        public async Task RunAsync()
        {
            // 启动 25 秒后再开始，避开首次引导、网关启动和界面加载。
            await Task.Delay(TimeSpan.FromSeconds(25));
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

            try
            {
                // 固定 1 小时的节奏：托盘常驻的机器可能几天不重启，只在启动时查一次就永远等不到新版本；
                // 查得太勤也没必要。每轮内部条件不合适就直接结束本轮，下一小时再试。
                var firstRound = true;
                while (true)
                {
                    if (!firstRound) await Task.Delay(TimeSpan.FromHours(1));
                    firstRound = false;

                    // 单轮异常不能带走整个循环，否则一次网络抖动就再也不检查了。
                    try { await RunOneRoundAsync(); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _log($"[Update] 静默更新流程异常: {ex.GetType().Name} - {ex.Message}"); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>
        /// 跑一轮：检查版本 → 条件合适就下载并静默安装。
        /// 任何一步条件不满足都直接结束本轮（由 RunAsync 安排下一小时再试）。
        /// </summary>
        private async Task RunOneRoundAsync()
        {
            if (!_config.AutoCheckUpdate) return;

            // 上次已下载但没装成：先续装，不再重新检查版本。
            if (await TryResumePendingUpdateAsync()) return;

            var info = await UpdateChecker.CheckLatestAsync(_config.UpdateSource, _log, CancellationToken.None);
            if (info == null || string.IsNullOrWhiteSpace(info.Version)) return;
            if (UpdateChecker.CompareVersions(info.Version, CurrentAppVersion) <= 0) return;

            // 同一个版本已经自动装过一次却没生效（安装器被安全软件拦下、安装目录被占用、用户点了取消），
            // 就别再自动来第二遍 —— 否则每次启动都会重新下载 + 拉起安装器。
            // 这里必须比 FailedUpdateVersion：待更新状态一旦被清理，PendingUpdateVersion 会一起清空。
            if (string.Equals(_config.FailedUpdateVersion, info.Version, StringComparison.OrdinalIgnoreCase))
            {
                _log($"[Update] v{info.Version} 之前自动安装未生效，已跳过自动更新；可手动运行安装包完成升级");
                return;
            }

            _log($"[Update] 静默更新发现新版本 {info.Tag}（当前 {CurrentAppVersion}）");
            if (!_config.AutoInstallAfterDownload)
            {
                _log("[Update] 静默更新：自动安装已关闭，只记录新版本");
                return;
            }

            // 主界面开着就不装：安装要重启程序，用户会看到界面「自己消失」——那其实只是去升级了，
            // 但观感上比晚一小时更新糟糕得多。先只提示，等他关掉界面后下一轮自动完成。
            if (_isMainWindowOpen())
            {
                _log("[Update] 主界面正在打开，本次不安装；关闭主界面后由下一轮自动更新");
                _notifyUi?.Invoke($"新版本 v{info.Version} 已就绪，关闭主界面后会自动更新", null);
                return;
            }

            // 希沃正在用网关时不装更新：安装要重启程序，会把正在登录的老师中断，而且那种时候 SSO
            // 请求量本来就高。判据是「最近 10 分钟内有 SSOLOGIN」——等下一轮（1 小时后）再说。
            if (SsologinRecentlyUsed(out var sinceMinutes))
            {
                _log($"[Update] 希沃 {sinceMinutes:F0} 分钟前刚用过 SSO，本次跳过自动更新，1 小时后再看");
                return;
            }

            if (string.IsNullOrWhiteSpace(info.SetupUrl) || string.IsNullOrWhiteSpace(info.Sha256))
            {
                _log("[Update] 静默更新：缺少安装包直链或 SHA256，跳过自动下载");
                return;
            }

            _setTrayStatus($"正在后台下载 v{info.Version}");
            _log($"[Update] 静默更新开始后台下载：{info.SetupUrl}");
            // 下载进度同时给托盘和主界面（界面开着的时候能看到进度条，而不是只盯着托盘）
            var progress = new Progress<(long received, long total)>(p =>
            {
                var percent = p.total > 0 ? (double?)Math.Round(p.received * 100.0 / p.total, 1) : null;
                _setTrayStatus(percent.HasValue
                    ? $"正在后台下载 v{info.Version} {percent:F0}%"
                    : $"正在后台下载 v{info.Version}");
                _notifyUi?.Invoke(
                    $"正在后台下载更新 v{info.Version}" + (percent.HasValue ? $"（{percent:F0}%）" : ""),
                    percent);
            });
            var localPath = await DownloadAccelerator.DownloadAsync(
                info.SetupUrl, info.Sha256, progress,
                msg => _log($"[Update] {msg}"), CancellationToken.None);

            // 下载可能花掉几分钟，装之前再确认一次希沃有没有开始用。
            if (SsologinRecentlyUsed(out var sinceMinutes2))
            {
                _log($"[Update] 下载期间希沃开始使用 SSO（{sinceMinutes2:F0} 分钟前），本次不安装，1 小时后再看");
                return;
            }

            var check = await Task.Run(() => UpdateInstaller.VerifyPackage(localPath, info.Sha256, _log));
            if (!check.Verified || !check.CanSilentInstall)
            {
                _log($"[Update] 静默更新：安装包校验未通过（{check.Reason}），取消自动安装");
                return;
            }

            _config.PendingUpdateVersion = info.Version;
            _config.PendingUpdatePath = localPath;
            _config.PendingUpdateSha256 = info.Sha256;
            // 直接标记成 installing（安装器紧接着就要拉起来了）。这样万一这次安装没生效，
            // 下次启动只会提示、不再重复安装 —— 否则就是安装—重启的循环。
            _config.PendingUpdateStage = "installing";
            _saveConfig();

            _setTrayStatus($"正在静默安装 v{info.Version}");
            _log($"[Update] 静默更新准备安装 v{info.Version}");
            // 安装前建立备份与 watcher：新版本 60 秒内未确认健康就自动回滚。
            UpdateHealthGuard.BeginGuard(Process.GetCurrentProcess().MainModule?.FileName, info.Version, _log);
            if (!UpdateInstaller.StartSilentInstall(check, _log))
            {
                _log("[Update] 静默更新：安装器启动失败，安装包已保留，下次启动会重试");
                return;
            }

            await Task.Delay(1000);
            _invokeOnUi(_requestExit);
        }

        /// <summary>希沃最近是否在用 SSO 网关（10 分钟内有 SSOLOGIN 请求）。</summary>
        private static bool SsologinRecentlyUsed(out double sinceMinutes)
        {
            var last = SeewoSsoGateway.LastSsologinUtc;
            if (last == DateTime.MinValue)
            {
                sinceMinutes = double.MaxValue;
                return false;
            }
            sinceMinutes = (DateTime.UtcNow - last).TotalMinutes;
            return sinceMinutes < 10;
        }

        /// <summary>清空待更新状态并落盘。</summary>
        public void ClearPendingState()
        {
            try
            {
                _config.PendingUpdatePath = "";
                _config.PendingUpdateSha256 = "";
                _config.PendingUpdateVersion = "";
                _config.PendingUpdateStage = "";
                _saveConfig();
            }
            catch (Exception ex)
            {
                _log($"[Update] 清理待更新状态失败: {ex.Message}");
            }
        }

        /// <summary>恢复上次已经下载但尚未安装的更新包。</summary>
        private async Task<bool> TryResumePendingUpdateAsync()
        {
            var stage = _config.PendingUpdateStage ?? "";
            if (string.IsNullOrWhiteSpace(stage)) return false;

            var target = UpdateChecker.NormalizeVersion(_config.PendingUpdateVersion);

            // 先看版本：已经装到位（或更高）就直接收尾。
            // 少了这一步，只要安装器没能把版本号换掉，每次启动都会再拉一次安装程序 ——
            // 表现就是「反复重启 + 一直卡 + 很快进入安全模式」。
            if (!string.IsNullOrWhiteSpace(target) &&
                UpdateChecker.CompareVersions(CurrentAppVersion, target) >= 0)
            {
                _log($"[Update] 当前版本 {CurrentAppVersion} 已不低于待安装的 {target}，清除待更新状态");
                ClearPendingState();
                return false;
            }

            if (!string.Equals(stage, "downloaded", StringComparison.OrdinalIgnoreCase))
            {
                // 停留在 "installing"：上一次已经拉起过安装程序，但版本没变，说明这次静默安装没成功
                // （常见于安装被安全软件拦截、安装目录被占用）。这种情况**不再自动重试**，
                // 只提示用户手动装，避免陷入安装—重启的死循环。
                _log($"[Update] v{target} 的静默安装上次未生效（状态 {stage}），已停止自动重试；"
                     + $"可手动运行安装包完成升级：{_config.PendingUpdatePath}");
                // 记在 FailedUpdateVersion 上再清状态：这个字段不随清理一起丢，
                // 下次检查更新时才能认出「这版装过但没成」，不再重复下载。
                _config.FailedUpdateVersion = target;
                ClearPendingState();
                return false;
            }

            var path = _config.PendingUpdatePath;
            var sha = _config.PendingUpdateSha256;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _log("[Update] 待安装的更新包已不存在，清除待更新状态");
                ClearPendingState();
                return false;
            }

            var check = await Task.Run(() => UpdateInstaller.VerifyPackage(path, sha, _log));
            if (!check.Verified || !check.CanSilentInstall)
            {
                _log($"[Update] 待安装更新包校验未通过（{check.Reason}），清除待更新状态");
                ClearPendingState();
                return false;
            }

            _log($"[Update] 恢复未完成的静默更新：v{target}");
            _config.PendingUpdateStage = "installing";
            _saveConfig();

            if (!UpdateInstaller.StartSilentInstall(check, _log)) return false;

            await Task.Delay(800);
            _invokeOnUi(_requestExit);
            return true;
        }

        /// <summary>当前程序版本（三段式）。</summary>
        private static string CurrentAppVersion
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }
    }
}