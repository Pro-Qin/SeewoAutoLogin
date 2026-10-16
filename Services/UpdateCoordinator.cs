using System;
using System.Collections.Generic;
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
        private readonly Action<SilentUpdateSnapshot> _notifyUi;
        private readonly Func<bool> _isMainWindowOpen;
        private int _running;

        public UpdateCoordinator(
            PluginConfig config,
            Action<string> log,
            Action saveConfig,
            Action<string> setTrayStatus,
            Action requestExit,
            Action<Action> invokeOnUi,
            Action<SilentUpdateSnapshot> notifyUi = null,
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
                _notifyUi?.Invoke(new SilentUpdateSnapshot
                {
                    Stage = "ready",
                    Version = info.Version,
                    Text = $"新版本 v{info.Version} 已就绪，关闭主界面后会自动更新"
                });
                return;
            }

            // 希沃正在用网关时不装更新：安装要重启程序，会把正在登录的老师中断，而且那种时候 SSO
            // 请求量本来就高。判据是「最近 10 分钟内有 SSOLOGIN」——等下一轮（1 小时后）再说。
            if (SsologinRecentlyUsed(out var sinceMinutes))
            {
                _log($"[Update] 希沃 {sinceMinutes:F0} 分钟前刚用过 SSO，本次跳过自动更新，1 小时后再看");
                return;
            }

            // 分流：安装版走 Inno 静默安装；便携版没有安装器接管文件，只能自己替换自己 ——
            // 否则新版会被装进 Program Files，用户却还在跑解压目录里的旧 exe，
            // 表现就是「一直提示更新、版本永远是老的」。
            var kind = UpdateChannel.Detect();
            var isPortable = kind == InstallKind.Portable;
            var downloadUrl = isPortable ? info.PortableUrl : info.SetupUrl;
            // 便携版必须用便携包自己的哈希：拿安装包的哈希去校验它，只会每次都失败。
            // 取不到便携包哈希时退回安装包哈希（至少下载流程一致），日志里能看出走了哪条路。
            // 便携版必须用便携包自己的哈希。拿安装包的哈希去校验便携包只会每次都失败，
            // 而且会一路重试到「所有下载源均不可用」；没有便携包哈希时按「没有可用哈希」处理，直接跳过本轮。
            var downloadSha = isPortable ? info.PortableSha256 : info.Sha256;
            if (isPortable && string.IsNullOrWhiteSpace(info.PortableSha256))
                _log("[Update] 便携版更新包在发布里没有独立哈希，本轮不做自动更新（可到发布页手动下载）");

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                _log($"[Update] 静默更新：缺少{(isPortable ? "便携版更新包" : "安装包")}直链，跳过自动下载");
                return;
            }
            if (string.IsNullOrWhiteSpace(downloadSha))
            {
                _log($"[Update] 静默更新：{(isPortable ? "便携版更新包" : "安装包")}没有可用的 SHA256，"
                     + "为安全起见不做自动更新（可到发布页手动下载）");
                return;
            }

            _setTrayStatus($"正在后台下载 v{info.Version}");
            _log($"[Update] 静默更新开始后台下载（{UpdateChannel.Describe(kind)}）：{downloadUrl}");
            // 下载进度同时给托盘和主界面（界面开着的时候能看到进度条，而不是只盯着托盘）。
            // 托盘按 5% 的台阶更新：进度回调是毫秒级的，每回调一次就改一次悬停提示，
            // 等于让 Shell 反复刷新通知区，任务栏图标会跟着闪。
            var lastTrayStep = -1;
            ResetRateSampling();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            var downloadToken = _downloadCts.Token;

            var progress = new Progress<(long received, long total)>(p =>
            {
                var percent = p.total > 0 ? (double?)Math.Round(p.received * 100.0 / p.total, 1) : null;
                var snapshot = PublishDownloading(info.Version, p.received, p.total, percent, SampleRate(p.received));

                var step = percent.HasValue ? (int)(percent.Value / 5) : -1;
                if (step != lastTrayStep)
                {
                    lastTrayStep = step;
                    _setTrayStatus(percent.HasValue
                        ? $"正在后台下载 v{info.Version} {percent:F0}%"
                        : $"正在后台下载 v{info.Version}");
                }
                _notifyUi?.Invoke(snapshot);
            });

            string localPath;
            _downloadFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // 注意：SHA256 强校验在 DownloadAsync 内部完成
                localPath = await DownloadAccelerator.DownloadAsync(
                    downloadUrl, downloadSha, progress,
                    msg => _log($"[Update] {msg}"), downloadToken);
            }
            catch (OperationCanceledException)
            {
                // 多半是用户在前台点了「下载新版本」而这一条太慢：前台会自己重下，这里安静收场。
                _log($"[Update] 后台下载 v{info.Version} 已取消");
                return;
            }
            finally
            {
                _downloadFinished?.TrySetResult(true);
                _downloadFinished = null;
                _downloadCts?.Dispose();
                _downloadCts = null;
                PublishIdle();
            }

            // 下载可能花掉几分钟，装之前再确认一次希沃有没有开始用。
            if (SsologinRecentlyUsed(out var sinceMinutes2))
            {
                _log($"[Update] 下载期间希沃开始使用 SSO（{sinceMinutes2:F0} 分钟前），本次不安装，1 小时后再看");
                return;
            }

            // 同样是因为下载耗时：用户完全可能在这期间把主界面打开了。
            // 不复查的话，界面会在老师眼前「自己消失」（安装要重启程序）—— 正是上面那段注释想避免的。
            if (_isMainWindowOpen())
            {
                _log("[Update] 下载期间主界面被打开，本次不安装；关闭主界面后由下一轮完成");
                _notifyUi?.Invoke(new SilentUpdateSnapshot
                {
                    Stage = "ready",
                    Version = info.Version,
                    Text = $"新版本 v{info.Version} 已就绪，关闭主界面后会自动更新"
                });
                return;
            }

            if (isPortable)
            {
                // 便携版：整理 staging → 备份目录 → 脚本替换 → 退出。
                // 备份由脚本自己做（整个程序目录复制到 backup\<版本>），不依赖 UpdateHealthGuard。
                var staging = PortableUpdater.PrepareStaging(localPath, _log);
                if (string.IsNullOrWhiteSpace(staging)) return;

                _config.PendingUpdateVersion = info.Version;
                _config.PendingUpdatePath = localPath;
                // 存**实际下载的那个包**的哈希：便携版下的是便携包，把安装包哈希写进来，
                // 后续任何一次复核都会必然失败（续装分支会因此直接清状态）。
                _config.PendingUpdateSha256 = downloadSha;
                _config.PendingUpdateStage = UpdateStages.Installing;
                RecordUpgradeNotice(info);
                _saveConfig();

                _setTrayStatus($"正在替换便携版 v{info.Version}");
                _notifyUi?.Invoke(new SilentUpdateSnapshot
                {
                    Stage = "installing",
                    Version = info.Version,
                    Text = $"正在更新到 v{info.Version}（会保留旧版本备份）"
                });
                _log($"[Update] 便携版自更新：目标目录 {UpdateChannel.CurrentDirectory}，备份到 {PortableUpdater.BackupRoot}\\{info.Version}");

                if (!PortableUpdater.LaunchReplaceScript(staging, info.Version, _log))
                {
                    // 状态留在 installing 会被下次启动当成「装过但没成」，这个版本从此不再自动更新。
                    // 脚本压根没跑起来，等于什么都没发生，把状态退回去。
                    _config.PendingUpdateStage = "";
                    _config.PendingUpdateVersion = "";
                    _config.PendingUpdatePath = "";
                    _config.PendingUpdateSha256 = "";
                    _saveConfig();
                    _log("[Update] 便携版自更新：替换脚本未能启动，已取消本次更新并回退状态");
                    return;
                }

                await Task.Delay(1000);
                _invokeOnUi(_requestExit);
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
            _config.PendingUpdateStage = UpdateStages.Installing;
            RecordUpgradeNotice(info);
            _saveConfig();

            _setTrayStatus($"正在静默安装 v{info.Version}");
            _log($"[Update] 静默更新准备安装 v{info.Version}");
            // 安装前建立备份与 watcher：新版本 60 秒内未确认健康就自动回滚。
            UpdateHealthGuard.BeginGuard(Process.GetCurrentProcess().MainModule?.FileName, info.Version, _log);
            if (!UpdateInstaller.StartSilentInstall(check, _log))
            {
                // 安装器压根没起来，更新等于没开始。此时必须立刻撤掉回滚保护，
                // 否则 watcher 150 秒后超时，会把这台正在正常干活的机器杀掉并用备份覆盖回去。
                UpdateHealthGuard.ConfirmHealthy(_log);
                _log("[Update] 静默更新：安装器启动失败，已撤销回滚保护，安装包保留待下次重试");
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

        /// <summary>更新内容写进配置时的长度上限：config.json 是整体重写的文本，不适合塞进整篇发布说明。</summary>
        private const int MaxNoticeNotesChars = 4000;

        /// <summary>
        /// 记录本次升级的起止版本、时间与更新内容，供新版本第一次打开主界面时提示一次。
        ///
        /// 时机选在「即将安装 / 替换」这一刻：此时目标版本与更新内容都已确定，而程序马上要退出重启，
        /// 新版本启动后就靠这份记录把「从什么版本升到了什么版本」讲清楚。
        /// </summary>
        private void RecordUpgradeNotice(UpdateInfo info)
        {
            try
            {
                if (info == null || string.IsNullOrWhiteSpace(info.Version)) return;

                _config.UpgradeNoticeFromVersion = CurrentAppVersion;
                _config.UpgradeNoticeToVersion = info.Version;
                _config.UpgradeNoticeAtUtc = DateTimeOffset.UtcNow;
                _config.UpgradeNoticeNotes = TrimNoticeNotes(info.Notes);
                // 这是一次新的升级，允许再提示一次
                _config.UpgradeNoticeShown = false;
                _log($"[Update] 已记录升级提醒：{CurrentAppVersion} → {info.Version}");
            }
            catch (Exception ex)
            {
                _log($"[Update] 记录升级提醒失败: {ex.Message}");
            }
        }

        private static string TrimNoticeNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes)) return "";
            var text = notes.Trim();
            return text.Length <= MaxNoticeNotesChars
                ? text
                : text.Substring(0, MaxNoticeNotesChars) + "\n…";
        }

        #region 对外状态

        private readonly object _stateGate = new object();
        private SilentUpdateSnapshot _snapshot = new SilentUpdateSnapshot();
        private CancellationTokenSource _downloadCts;
        private TaskCompletionSource<bool> _downloadFinished;
        private readonly Queue<(DateTime AtUtc, long Received)> _rateWindow = new Queue<(DateTime, long)>();
        private double _lastRate;

        /// <summary>当前后台更新状态的一份值拷贝。</summary>
        public SilentUpdateSnapshot Snapshot()
        {
            lock (_stateGate) return _snapshot.Clone();
        }

        /// <summary>是否正在下载更新包。</summary>
        public bool IsDownloading => Snapshot().IsDownloading;

        /// <summary>
        /// 掐断后台正在进行的下载。等到下载循环真正退出才返回 ——
        /// 否则前台会立刻用同一个文件名重新下载，两个任务写同一个文件。
        /// </summary>
        public async Task<bool> AbortDownloadAsync(string reason, int waitMs = 8000)
        {
            CancellationTokenSource cts;
            TaskCompletionSource<bool> finished;
            lock (_stateGate)
            {
                cts = _downloadCts;
                finished = _downloadFinished;
            }

            if (cts == null || cts.IsCancellationRequested) return false;

            _log($"[Update] {reason} —— 掐断后台下载");
            try { cts.Cancel(); } catch { }

            if (finished != null)
            {
                try { await Task.WhenAny(finished.Task, Task.Delay(waitMs)); } catch { }
            }

            PublishIdle();
            return true;
        }

        private SilentUpdateSnapshot PublishDownloading(string version, long received, long total, double? percent, double rate)
        {
            var snapshot = new SilentUpdateSnapshot
            {
                Stage = "downloading",
                Version = version ?? "",
                Received = received,
                Total = total,
                Percent = percent,
                BytesPerSecond = rate,
                Text = $"正在后台下载 v{version}"
            };
            lock (_stateGate) _snapshot = snapshot;
            return snapshot;
        }

        private void PublishIdle()
        {
            lock (_stateGate) _snapshot = new SilentUpdateSnapshot();
            ResetRateSampling();
        }

        /// <summary>
        /// 速率采样：看最近 3 秒的窗口。窗口跨度不足 1 秒时沿用上一次的结果，
        /// 否则开头几次回调会算出离谱的速率，前台据此判断「要不要接手」就会翻车。
        /// </summary>
        private double SampleRate(long received)
        {
            var now = DateTime.UtcNow;
            lock (_stateGate)
            {
                _rateWindow.Enqueue((now, received));
                while (_rateWindow.Count > 2 && (now - _rateWindow.Peek().AtUtc).TotalSeconds > 3)
                    _rateWindow.Dequeue();

                if (_rateWindow.Count >= 2)
                {
                    var first = _rateWindow.Peek();
                    var seconds = (now - first.AtUtc).TotalSeconds;
                    if (seconds >= 1) _lastRate = (received - first.Received) / seconds;
                }
                return _lastRate;
            }
        }

        private void ResetRateSampling()
        {
            lock (_stateGate)
            {
                _rateWindow.Clear();
                _lastRate = 0;
            }
        }

        #endregion

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

            if (!string.Equals(stage, UpdateStages.Downloaded, StringComparison.OrdinalIgnoreCase))
            {
                // 停留在 installing：上一次已经拉起过安装程序，但版本没变，说明这次静默安装没成功
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