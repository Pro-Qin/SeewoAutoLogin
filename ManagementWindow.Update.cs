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
        #region 更新检查

        private bool _updateChecked;
        /// <summary>最近一次检查到的更新信息（下载时复用其直链与校验值）</summary>
        private UpdateInfo _latestUpdate;
        /// <summary>当前更新包下载的取消源（供界面「取消下载」使用）</summary>
        private CancellationTokenSource _downloadCts;

        /// <summary>
        /// 后台静默更新的进度回显。由 UpdateCoordinator 在下载/安装阶段调用：
        /// 主界面没打开时自然什么也不做，开着的时候就能看到同一条进度与进度条。
        /// </summary>
        internal void OnSilentUpdateProgress(SilentUpdateSnapshot progress)
        {
            if (progress == null || !IsLoaded) return;
            try
            {
                if (progress.IsDownloading)
                {
                    // 后台下载复用前台的进度渲染：received/total 齐全，界面据此画进度条
                    _ = SendToJs(new
                    {
                        type = "update-progress",
                        state = "downloading",
                        received = progress.Received,
                        total = progress.Total,
                        background = true
                    });
                }
                else if (!string.IsNullOrWhiteSpace(progress.Text))
                {
                    _ = SendToJs(new
                    {
                        type = "update-progress",
                        state = progress.Stage,
                        text = progress.Text,
                        background = true
                    });
                }
            }
            catch { }
        }

        private async Task HandleCheckUpdateAsync(bool manual)
        {
            // 界面每次加载都会自动查一次，关掉窗口再打开又会重来一遍，没必要反复打接口：
            // 自动检查 1 小时内只做一次（与后台轮询同节奏）。手动点「检查更新」不受限制。
            if (!manual)
            {
                var last = _app.Config.LastAutoUpdateCheckUtc;
                if (last.HasValue && DateTimeOffset.UtcNow - last.Value < TimeSpan.FromHours(1))
                {
                    _app.WriteDiagnosticLog($"[Update] 距上次自动检查不足 1 小时，跳过本次（上次 {last.Value.ToLocalTime():yyyy-MM-dd HH:mm}）");
                    return;
                }
            }

            if (manual) await SendToJs(new { type = "update-status", text = "正在检查更新…", state = "" });
            if (!manual && _updateChecked) return;
            _updateChecked = true;

            if (!manual)
            {
                _app.Config.LastAutoUpdateCheckUtc = DateTimeOffset.UtcNow;
                _app.SaveConfig();
            }

            var current = AppVersion;
            try
            {
                var info = await UpdateChecker.CheckLatestAsync(_app.Config.UpdateSource, _app.WriteDiagnosticLog,
                    CancellationToken.None);
                var hasUpdate = UpdateChecker.CompareVersions(info.Version, current) > 0;

                if (hasUpdate)
                {
                    _app.WriteDiagnosticLog($"[Update] 发现新版本 {info.Tag}（当前 {current}）；更新源={info.Source}");
                    _latestUpdate = info;
                    var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n" + info.Notes.Trim();
                    await SendToJs(new
                    {
                        type = "update-status",
                        state = "new",
                        hasUpdate = true,
                        latest = info.Version,
                        downloadUrl = string.IsNullOrEmpty(info.SetupUrl) ? info.PageUrl : info.SetupUrl,
                        pageUrl = info.PageUrl,
                        text = $"发现新版本 v{info.Version}（当前 v{current}）\n更新源：{info.Source}{notes}"
                    });
                    _app.TrayIcon?.SetStatusText($"发现新版本 v{info.Version}");
                }
                else
                {
                    await SendToJs(new
                    {
                        type = "update-status",
                        state = "ok",
                        hasUpdate = false,
                        latest = info.Version,
                        pageUrl = info.PageUrl,
                        text = $"已是最新版本（v{current}）\n更新源：{info.Source}"
                    });
                }
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Update] 检查更新失败: {ex.Message}");
                await SendToJs(new
                {
                    type = "update-status",
                    state = "error",
                    hasUpdate = false,
                    text = manual ? $"检查更新失败：{ex.Message}" : ""
                });
            }
        }

        /// <summary>
        /// 后台自动更新完成后，第一次打开主界面时提示一次「升级完成」。
        ///
        /// 只提示一次：消息发出后立即把标记落盘，避免每次打开界面都弹。
        /// 版本没到位（安装被安全软件拦下、被回滚、装到了别的目录）时什么都不做 ——
        /// 否则会出现「说升到了 X、版本却还是旧的」这种自相矛盾的提示。
        /// </summary>
        internal async Task SendUpgradeNoticeIfNeededAsync()
        {
            try
            {
                // 判定统一放在 App 侧（TryConsumeUpgradeNotice）：桌面提醒与界面内提醒共用同一份逻辑，
                // 所以两者不会同时弹 —— 先到的那边走完就把「已提示」标记消费掉了。
                var notice = _app?.TryConsumeUpgradeNotice(markShown: true);
                if (notice == null) return;

                await SendToJs(BuildUpgradeNoticePayload(notice.From, notice.To, notice.AtUtc, notice.Notes, false));
                _app.WriteDiagnosticLog(
                    $"[Update] 已提示升级完成：{(string.IsNullOrWhiteSpace(notice.From) ? "（无起始版本记录）" : notice.From)} → {notice.To}");
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Update] 发送升级提醒失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置页的「显示升级提醒」：只为调试动效与文案，展示一次即可，
        /// 不写入任何状态（否则一次预览就把真正的升级提示消耗掉了）。
        /// </summary>
        private async Task HandleDebugUpgradeNoticeAsync()
        {
            try
            {
                var notice = _app?.BuildPreviewUpgradeNotice();
                if (notice == null) return;

                await SendToJs(BuildUpgradeNoticePayload(notice.From, notice.To, notice.AtUtc, notice.Notes, true));
                _app.WriteDiagnosticLog("[Update] 调试预览界面升级提醒（不写入一次性提示状态）");
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[Update] 调试预览升级提醒失败: {ex.Message}");
            }
        }

        /// <summary>设置页的「桌面提醒预览」：把桌面级提醒窗口弹一次，同样不写入任何状态。</summary>
        private void HandleDebugDesktopNotice()
        {
            var notice = _app.BuildPreviewUpgradeNotice();
            UpgradeToastWindow.ShowNotice(notice);
            _app.WriteDiagnosticLog("[Update] 调试预览桌面升级提醒（不写入一次性提示状态）");
        }

        /// <summary>
        /// 升级提醒的下发内容。时间在 C# 侧就格式化成当地时间，前端不必处理时区与解析差异。
        /// </summary>
        private static object BuildUpgradeNoticePayload(string from, string to, DateTimeOffset? atUtc, string notes, bool debug)
            => new
            {
                type = "upgrade-notice",
                from = from ?? "",
                to = to ?? "",
                atText = (atUtc ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                notes = notes ?? "",
                debug = debug
            };

        /// <summary>允许在前端点击后打开的外部地址（仅 https，且限定这些主机）</summary>
        private static readonly string[] TrustedExternalHosts =
        {
            "space.bilibili.com", "www.bilibili.com", "github.com", "gitee.com", "pro-qin.github.io",
        };

        /// <summary>
        /// 处理前端发来的外链跳转请求（作者主页、项目仓库）。
        /// 前端消息不可信，这里按「https + 主机白名单」再校验一次，避免被改成 file:// 或任意网址。
        /// </summary>
        private void HandleOpenExternal(JsonElement root)
        {
            var url = root.TryGetProperty("url", out var element) ? element.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !TrustedExternalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                _app.WriteDiagnosticLog($"[External] 已拦截不可信的外链请求: {url}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[External] 打开链接失败: {ex.Message}");
            }
        }

        /// <summary>把用户协议正文下发给前端（「关于」页点击《用户协议》时请求，与欢迎界面共用同一份资源）</summary>
        private async Task SendTerms()
        {
            var text = App.LoadTermsText();
            if (string.IsNullOrWhiteSpace(text))
                _app.WriteDiagnosticLog("[Terms] 协议正文载入失败（嵌入资源缺失？）");
            await SendToJs(new { type = "terms", text });
        }

        private async void HandleOpenUpdatePage(JsonElement root)
        {
            var url = root.TryGetProperty("url", out var element) ? element.GetString() : null;
            // 该 URL 来自前端消息且会用 ShellExecute 打开，必须校验协议与主机，避免被篡改成 file:// 或 UNC 路径
            if (string.IsNullOrWhiteSpace(url) || !UpdateChecker.IsTrustedDownloadUrl(url))
            {
                if (!string.IsNullOrWhiteSpace(url))
                    _app.WriteDiagnosticLog($"[Update] 已拦截不可信的更新地址，改用发布页: {url}");
                // 优先用最近一次检查到的 tag 页面，直接落到对应版本
                url = string.IsNullOrWhiteSpace(_latestUpdate?.PageUrl)
                    ? UpdateChecker.ReleasesPageUrl
                    : _latestUpdate.PageUrl;
            }
            // github.com 在国内常打不开：先探测一个可达的镜像地址再打开
            var target = await UpdateChecker.ResolveBestPageUrlAsync(url, _app.WriteDiagnosticLog, CancellationToken.None);
            _app.WriteDiagnosticLog($"[Update] 打开发布页: {target}");
            OpenExternal(target, alreadyValidated: true);
        }

        /// <summary>
        /// 下载新版本：先按网络情况挑最快的加速源，下载完成后校验 SHA256 再启动安装程序。
        /// 直链不可用或下载失败时，回退到打开发布页让用户手动下载。
        /// </summary>
        private async void HandleDownloadUpdate()
        {
            // 后台可能正拿着同一个包在下载。先看它跑到哪了：
            // 够快就接过它的进度显示（前台不重复下载），慢就掐断它、由前台重下一遍 ——
            // 前台这条路有进度条，也有「取消下载」。
            var silent = _app.SilentUpdateSnapshot();
            if (silent.IsDownloading)
            {
                if (SilentUpdatePolicy.ShouldAdopt((long)silent.BytesPerSecond, silent.Percent))
                {
                    _app.WriteDiagnosticLog($"[Update] 后台下载进行中（{SilentUpdatePolicy.Describe(silent.BytesPerSecond, silent.Percent)}），改为在前台显示它的进度");
                    await SendToJs(new { type = "toast", text = "正在显示后台的下载进度", level = "info" });
                    OnSilentUpdateProgress(silent);
                    return;
                }

                _app.WriteDiagnosticLog($"[Update] 后台下载偏慢（{SilentUpdatePolicy.Describe(silent.BytesPerSecond, silent.Percent)}），掐断后台，改由前台重新下载");
                await _app.AbortSilentUpdateAsync("用户手动点「下载新版本」，后台这条太慢");
            }
            else if (silent.Stage == "installing")
            {
                // 已经在替换/安装，重复点一次只会再拉一遍同一个包
                await SendToJs(new { type = "toast", text = "更新包已在后台安装，完成后会提示你", level = "info" });
                return;
            }
            else
            {
                var pending = _app.PendingUpdateVersion;
                if (!string.IsNullOrWhiteSpace(pending))
                {
                    // 之前已经下好、在等安装时机（例如主界面开着）。重复下载只是把同一个包拉第二遍。
                    _app.WriteDiagnosticLog($"[Update] v{pending} 的更新包已在后台就绪，不重复下载");
                    await SendToJs(new
                    {
                        type = "update-progress",
                        state = "downloaded",
                        text = $"v{pending} 的更新包已下载完成，关闭主界面后会自动安装"
                    });
                    return;
                }
            }

            var info = _latestUpdate;
            if (info == null)
            {
                _app.WriteDiagnosticLog("[Update] 没有可用的更新信息，改为打开发布页");
                OpenExternal(UpdateChecker.ReleasesPageUrl);
                return;
            }

            // 按部署形态分流：便携版必须下载便携包并自己替换自己。
            // 若这里统一下安装包，便携版用户会被装进 Program Files，而他仍在跑解压目录里的旧程序 ——
            // 表现就是「一直提示更新、版本永远是老的」。后台路径已经这么分流了，手动入口不能例外。
            var kind = UpdateChannel.Detect();
            var isPortable = kind == InstallKind.Portable;
            var downloadUrl = isPortable ? info.PortableUrl : info.SetupUrl;
            // 便携版要拿便携包自己的哈希：用安装包哈希去校验便携包必然失败
            var downloadSha = isPortable && !string.IsNullOrWhiteSpace(info.PortableSha256)
                ? info.PortableSha256
                : info.Sha256;

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                _app.WriteDiagnosticLog($"[Update] 没有可用的{(isPortable ? "便携版更新包" : "安装包")}直链，改为打开发布页");
                OpenExternal(UpdateChecker.ReleasesPageUrl);
                return;
            }
            _app.WriteDiagnosticLog($"[Update] 手动更新：{UpdateChannel.Describe(kind)}，下载 {downloadUrl}");

            // 每次下载配一个独立的取消源，界面上的「取消下载」会取消它
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            try
            {
                await SendToJs(new { type = "update-progress", state = "preparing", text = "正在挑选最快的下载源…" });

                var progress = new Progress<(long received, long total)>(p =>
                {
                    _ = SendToJs(new
                    {
                        type = "update-progress",
                        state = "downloading",
                        received = p.received,
                        total = p.total
                    });
                });

                var localPath = await DownloadAccelerator.DownloadAsync(
                    downloadUrl,
                    downloadSha,
                    progress,
                    message => _app.WriteDiagnosticLog($"[Update] {message}"),
                    token);

                if (token.IsCancellationRequested) return; // 取消后不要接着启动安装程序

                _app.WriteDiagnosticLog($"[Update] 安装包已下载并校验通过: {localPath}");
                // 这一步可能由用户手动点「下载新版本」触发，弹一条通知；后台静默下载走 UpdateCoordinator，不经过这里
                _app.TrayIcon?.Notify("更新包下载完成");

                // 便携版没有安装向导可以交给用户：目录版必须整体替换，只能走自替换流程
                if (isPortable)
                {
                    await RunPortableSelfUpdateAsync(localPath, info, downloadSha);
                    return;
                }

                // 关掉自动安装时保持旧行为：把安装包交给用户，由他自己走安装向导
                if (!_app.Config.AutoInstallAfterDownload)
                {
                    _app.WriteDiagnosticLog("[Update] 「下载完成后自动安装」已关闭，改为打开安装包由用户手动安装");
                    await SendToJs(new { type = "update-progress", state = "done", text = "下载完成，已为你打开安装程序（自动安装已关闭）" });
                    await SendToJs(new { type = "toast", text = "更新包已下载，已为你打开安装程序（自动安装已关闭）", level = "info" });
                    OpenExternal(localPath);
                    return;
                }

                await TrySilentInstallAsync(localPath, info);
            }
            catch (Exception ex)
            {
                // 用户主动取消不被当成失败，也不回退到发布页
                if (token.IsCancellationRequested)
                {
                    _app.WriteDiagnosticLog("[Update] 用户取消了更新包下载");
                    await SendToJs(new { type = "update-progress", state = "cancelled", text = "已取消下载" });
                    return;
                }

                _app.WriteDiagnosticLog($"[Update] 下载失败: {ex.Message}");
                await SendToJs(new
                {
                    type = "update-progress",
                    state = "error",
                    text = "下载失败：" + ex.Message + "（已为你打开发布页，可手动下载）"
                });
                var fallback = await UpdateChecker.ResolveBestPageUrlAsync(
                    string.IsNullOrWhiteSpace(info.PageUrl) ? UpdateChecker.ReleasesPageUrl : info.PageUrl,
                    _app.WriteDiagnosticLog, CancellationToken.None);
                OpenExternal(fallback, alreadyValidated: true);
            }
            finally
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        /// <summary>
        /// 便携版手动更新：与后台路径共用同一套自替换流程（整理 staging → 备份 → 脚本替换 → 退出），
        /// 区别只是由用户点按钮触发，所以进度要留在界面上，而不是只写日志。
        /// </summary>
        private async Task RunPortableSelfUpdateAsync(string localPath, UpdateInfo info, string downloadSha)
        {
            await SendToJs(new { type = "update-progress", state = "verifying", text = "便携版正在准备替换文件…" });

            var staging = PortableUpdater.PrepareStaging(localPath, _app.WriteDiagnosticLog);
            if (string.IsNullOrWhiteSpace(staging))
            {
                _app.WriteDiagnosticLog("[Update] 便携版更新包整理失败，改为打开发布页由用户手动下载");
                await SendToJs(new { type = "update-progress", state = "error", text = "便携版更新包不可用，已为你打开发布页" });
                OpenExternal(UpdateChecker.ReleasesPageUrl);
                return;
            }

            // 待更新状态与后台路径保持一致：替换脚本马上要拉起，失败也不再自动重试
            _app.Config.PendingUpdateVersion = info.Version;
            _app.Config.PendingUpdatePath = localPath;
            _app.Config.PendingUpdateSha256 = downloadSha;
            _app.Config.PendingUpdateStage = UpdateStages.Installing;
            _app.SaveConfig();

            await SendToJs(new { type = "toast", text = "便携版即将替换文件并重新启动", level = "ok" });
            await SendToJs(new { type = "update-progress", state = "installing", text = "正在替换便携版文件，程序即将退出…" });

            if (!PortableUpdater.LaunchReplaceScript(staging, info.Version, _app.WriteDiagnosticLog))
            {
                _app.WriteDiagnosticLog("[Update] 便携版替换脚本未能启动，本次更新取消");
                await SendToJs(new { type = "update-progress", state = "error", text = "替换脚本未能启动，程序保持原样" });
                await SendToJs(new { type = "toast", text = "便携版替换未能启动，程序保持原样", level = "warn" });
                return;
            }

            // 留出时间让上面的提示渲染出来：随后程序会自己退出，由脚本接手替换
            await Task.Delay(900);
            _app.BeginSilentUpdateExit();
        }

        /// <summary>静默安装是否已进入「启动安装器 + 退出」阶段（防止重复拉起安装器）</summary>
        private bool _silentInstallStarted;

        /// <summary>
        /// 下载完成后的静默升级：先按官方 SHA256 复核安装包、确认它就在下载目录里，
        /// 再用 Inno Setup 静默参数拉起安装器，最后本程序主动退出（否则安装器替换不了正在运行的 exe）。
        ///
        /// 任何一项校验不通过都不会启动安装包：路径不可信就打开发布页，哈希不一致连打开都不做。
        /// </summary>
        private async Task TrySilentInstallAsync(string localPath, UpdateInfo info)
        {
            try
            {
                await SendToJs(new { type = "update-progress", state = "verifying", text = "安装包已就绪，正在校验…" });
                await SendToJs(new { type = "toast", text = "安装包已就绪，将在关闭程序后自动安装", level = "ok" });

                // 留出时间让提示渲染出来：随后程序会自己退出，用户看不到别的解释了
                await Task.Delay(1500);

                var check = await Task.Run(() => UpdateInstaller.VerifyPackage(localPath, info.Sha256, _app.WriteDiagnosticLog));

                if (!check.PathTrusted)
                {
                    // 位置不可信（不在下载目录 / 不是 exe / 文件已消失）：什么都不启动，回发布页
                    _app.WriteDiagnosticLog($"[Update] 安装包位置校验未通过，取消自动安装：{check.Reason}");
                    await SendToJs(new { type = "update-progress", state = "error", text = "安装包位置校验未通过：" + check.Reason });
                    await SendToJs(new { type = "toast", text = "安装包位置校验未通过，已为你打开发布页", level = "error" });
                    OpenExternal(UpdateChecker.ReleasesPageUrl);
                    return;
                }

                if (!check.HashVerified)
                {
                    // 两种都要挡住：哈希不符（可能被篡改）与根本没有官方哈希（无从校验）。
                    // 区别只在于提示措辞：前者要重新下载，后者是这个版本没提供清单。
                    _app.WriteDiagnosticLog($"[Update] 安装包未通过 SHA256 校验，取消自动安装：{check.Reason}");
                    var text = check.HashAvailable
                        ? "安装包校验未通过：" + check.Reason + "（未安装任何内容，请在发布页重新下载）"
                        : "该版本没有官方 SHA256 清单，已跳过自动安装（安装包已保留，可在发布页下载后手动安装）";
                    var toast = check.HashAvailable
                        ? "安装包校验未通过，已阻止自动安装，请在发布页重新下载"
                        : "该版本缺少官方 SHA256 清单，已跳过自动安装";
                    await SendToJs(new { type = "update-progress", state = "error", text });
                    await SendToJs(new { type = "toast", text = toast, level = check.HashAvailable ? "error" : "warn" });
                    OpenExternal(UpdateChecker.ReleasesPageUrl);
                    return;
                }

                if (!check.IsSetupPackage)
                {
                    // 单文件版 exe 不认识 /SILENT：只能像以前一样交给用户手动运行
                    _app.WriteDiagnosticLog("[Update] 更新包不是 Inno Setup 安装包，改为打开更新包由用户手动安装");
                    await SendToJs(new { type = "update-progress", state = "done", text = "下载完成，已为你打开更新包" });
                    await SendToJs(new { type = "toast", text = "该更新包不支持静默安装，已为你打开", level = "info" });
                    OpenExternal(check.FullPath);
                    return;
                }

                if (_silentInstallStarted) return;
                _silentInstallStarted = true;

                // 手动静默升级同样写入待更新状态：安装器带 --updated 启动后会自动提示版本并清理状态。
                _app.Config.PendingUpdateVersion = info.Version;
                _app.Config.PendingUpdatePath = localPath;
                _app.Config.PendingUpdateSha256 = info.Sha256;
                _app.Config.PendingUpdateStage = "downloaded";
                _app.SaveConfig();

                await SendToJs(new { type = "update-progress", state = "installing", text = "正在静默安装，本程序即将退出…" });

                if (!UpdateInstaller.StartSilentInstall(check, _app.WriteDiagnosticLog))
                {
                    // 例如用户在 UAC 弹窗上点了「否」：保留安装包，让用户手动完成
                    _silentInstallStarted = false;
                    await SendToJs(new { type = "update-progress", state = "error", text = "自动安装未能启动，安装包已保留，可手动运行" });
                    await SendToJs(new { type = "toast", text = "自动安装未能启动（可能取消了管理员授权），安装包已保留，可手动运行", level = "warn" });
                    return;
                }

                // 安装器已拉起：稍等一下让前端把提示渲染完，然后退出让安装器替换程序文件
                await Task.Delay(800);
                _app.BeginSilentUpdateExit();
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Update] 静默安装流程异常: {ex.Message}");
            }
        }

        /// <summary>取消正在进行的更新包下载（前台自己的，或者界面上正显示的后台那条）</summary>
        private void HandleCancelDownload()
        {
            try
            {
                if (_downloadCts != null && !_downloadCts.IsCancellationRequested)
                {
                    _downloadCts.Cancel();
                    _app.WriteDiagnosticLog("[Update] 收到取消下载请求");
                    return;
                }

                // 前台没有下载任务时，界面上的「取消下载」只可能来自后台那条进度
                if (_app.SilentUpdateSnapshot().IsDownloading)
                {
                    _app.WriteDiagnosticLog("[Update] 收到取消下载请求（后台静默下载）");
                    _ = _app.AbortSilentUpdateAsync("用户在界面取消下载");
                    return;
                }

                _app.WriteDiagnosticLog("[Update] 当前没有正在进行的下载");
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Update] 取消下载失败: {ex.Message}");
            }
        }

        /// <summary>打开本地文件或受信任的网页；不可信地址一律回落到官方发布页</summary>
        private void OpenExternal(string target, bool alreadyValidated = false)
        {
            try
            {
                if (File.Exists(target))
                {
                    Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                    return;
                }

                var url = (alreadyValidated || UpdateChecker.IsTrustedDownloadUrl(target))
                    ? target
                    : UpdateChecker.ReleasesPageUrl;
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _app.WriteDiagnosticLog($"[Update] 打开失败: {ex.Message}");
            }
        }

        #endregion
    }
}
