using System;
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
        private int _running;

        public UpdateCoordinator(
            PluginConfig config,
            Action<string> log,
            Action saveConfig,
            Action<string> setTrayStatus,
            Action requestExit,
            Action<Action> invokeOnUi)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? (_ => { });
            _saveConfig = saveConfig ?? (() => { });
            _setTrayStatus = setTrayStatus ?? (_ => { });
            _requestExit = requestExit ?? (() => { });
            _invokeOnUi = invokeOnUi ?? (action => action());
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
                if (!_config.AutoCheckUpdate) return;

                // 上次已下载但没装成：先续装，不再重新检查版本。
                if (await TryResumePendingUpdateAsync()) return;

                var info = await UpdateChecker.CheckLatestAsync(_config.UpdateSource, _log, CancellationToken.None);
                if (info == null || string.IsNullOrWhiteSpace(info.Version)) return;
                if (UpdateChecker.CompareVersions(info.Version, CurrentAppVersion) <= 0) return;

                _log($"[Update] 静默更新发现新版本 {info.Tag}（当前 {CurrentAppVersion}）");
                if (!_config.AutoInstallAfterDownload)
                {
                    _log("[Update] 静默更新：自动安装已关闭，只记录新版本");
                    return;
                }
                if (string.IsNullOrWhiteSpace(info.SetupUrl) || string.IsNullOrWhiteSpace(info.Sha256))
                {
                    _log("[Update] 静默更新：缺少安装包直链或 SHA256，跳过自动下载");
                    return;
                }

                _setTrayStatus($"正在后台下载 v{info.Version}");
                _log($"[Update] 静默更新开始后台下载：{info.SetupUrl}");
                var progress = new Progress<(long received, long total)>(_ => { });
                var localPath = await DownloadAccelerator.DownloadAsync(
                    info.SetupUrl, info.Sha256, progress,
                    msg => _log($"[Update] {msg}"), CancellationToken.None);

                var check = await Task.Run(() => UpdateInstaller.VerifyPackage(localPath, info.Sha256, _log));
                if (!check.Verified || !check.CanSilentInstall)
                {
                    _log($"[Update] 静默更新：安装包校验未通过（{check.Reason}），取消自动安装");
                    return;
                }

                _config.PendingUpdateVersion = info.Version;
                _config.PendingUpdatePath = localPath;
                _config.PendingUpdateSha256 = info.Sha256;
                _config.PendingUpdateStage = "downloaded";
                _saveConfig();

                _setTrayStatus($"正在静默安装 v{info.Version}");
                _log($"[Update] 静默更新准备安装 v{info.Version}");
                // 安装前建立备份与 watcher：新版本 60 秒内未确认健康就自动回滚。
                UpdateHealthGuard.BeginGuard(Environment.ProcessPath, info.Version, _log);
                if (!UpdateInstaller.StartSilentInstall(check, _log))
                {
                    _log("[Update] 静默更新：安装器启动失败，安装包已保留，下次启动会重试");
                    return;
                }

                await Task.Delay(1000);
                _invokeOnUi(_requestExit);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log($"[Update] 静默更新流程异常: {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
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
            if (!string.Equals(_config.PendingUpdateStage, "downloaded", StringComparison.OrdinalIgnoreCase))
                return false;

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

            _log($"[Update] 恢复未完成的静默更新：v{_config.PendingUpdateVersion}");
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