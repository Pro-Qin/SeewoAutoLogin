using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// WebView2 运行时（Runtime）探测、自动安装与环境缓存。
    ///
    /// 生产环境白屏的两个根因都在这里处理：
    ///  1. 目标机器没装 WebView2 运行时 → 自动静默安装官方 Evergreen 运行时（用户零操作）；
    ///  2. 应用安装在 Program Files，WebView2 默认在 exe 同级建用户数据目录会失败
    ///     → 用户数据目录固定到 %LOCALAPPDATA%\SeewoAutoLogin\WebView2。
    /// </summary>
    internal static class WebView2Runtime
    {
        /// <summary>微软官方 Evergreen Bootstrapper（约 150KB，运行时由它联网下载并静默安装）。</summary>
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>官方 WebView2 下载页（自动安装失败时供用户手动安装）。</summary>
        public const string ManualDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        /// <summary>EdgeUpdate 客户端注册表项中 WebView2 运行时的固定 GUID。</summary>
        private const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        private static readonly object Gate = new object();
        private static Task<CoreWebView2Environment> _environmentTask;

        /// <summary>WebView2 用户数据目录（放在 LOCALAPPDATA，避免 Program Files 写权限问题）。</summary>
        public static string UserDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "WebView2");

        /// <summary>
        /// 获取已安装的 WebView2 运行时版本；未安装返回 null。
        /// 先问 WebView2 API（等价于真实可用性），失败再查注册表兜底。
        /// </summary>
        public static string GetInstalledVersion()
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
            catch
            {
                // 运行时缺失时该 API 会抛 WebView2RuntimeNotFoundException，转注册表兜底
            }

            try
            {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientId);
                        var pv = key?.GetValue("pv") as string;
                        if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return pv;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public static bool IsInstalled => !string.IsNullOrWhiteSpace(GetInstalledVersion());

        /// <summary>创建并缓存 WebView2 环境（浏览器进程启动较慢，缓存后窗口重建无需重新初始化）。</summary>
        public static Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            lock (Gate)
            {
                if (_environmentTask == null)
                    _environmentTask = CreateEnvironmentAsync();
                return _environmentTask;
            }
        }

        /// <summary>丢弃环境缓存（安装运行时后、或缓存任务已失败时必须调用）。</summary>
        public static void ResetEnvironment()
        {
            lock (Gate)
            {
                _environmentTask = null;
            }
        }

        /// <summary>
        /// 预热：应用启动时提前在后台创建环境与浏览器进程，缩短首次打开主界面的等待。
        /// 失败只记日志，不抛异常（运行时缺失由主界面负责自动安装）。
        /// </summary>
        public static void Prewarm(Action<string> log)
        {
            try
            {
                GetEnvironmentAsync().ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        log?.Invoke($"[WebView2] 预热失败: {task.Exception?.GetBaseException().Message}");
                        ResetEnvironment();
                    }
                    else
                    {
                        log?.Invoke("[WebView2] 运行时预热完成");
                    }
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WebView2] 预热异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 下载官方 Bootstrapper 并静默安装 WebView2 运行时。
        /// 返回 true 表示安装后已能探测到运行时。
        /// </summary>
        public static async Task<bool> InstallAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (IsInstalled) return true;

            var setupPath = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebView2Setup.exe");
            try
            {
                progress?.Report("正在下载 WebView2 运行时安装包…");
                await DownloadFileAsync(BootstrapperUrl, setupPath, progress, cancellationToken);

                progress?.Report("正在安装 WebView2 运行时（静默模式，可能需要 1-2 分钟）…");
                var startInfo = new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = "/silent /install",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    var exited = await Task.Run(() => process.WaitForExit(300_000), cancellationToken);
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }
                }

                // 安装完成后旧环境缓存已失效
                ResetEnvironment();
                progress?.Report("安装完成，正在校验运行时…");
                return IsInstalled;
            }
            finally
            {
                try { if (File.Exists(setupPath)) File.Delete(setupPath); } catch { }
            }
        }

        private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            Directory.CreateDirectory(UserDataFolder);
            return CoreWebView2Environment.CreateAsync(null, UserDataFolder, null);
        }

        private static async Task DownloadFileAsync(string url, string destination, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            using var handler = new HttpClientHandler();
            // 代理软件未运行但系统里残留代理设置时会导致下载必然失败；这里自动回退直连。
            NetworkRoute.ConfigureHandler(handler, new Uri(url));
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SeewoAutoLogin");

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = 0;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer, 0, read, cancellationToken);
                received += read;
                if (total <= 0) continue;
                var percent = (int)(received * 100 / total);
                if (percent == lastPercent || percent % 5 != 0) continue;
                lastPercent = percent;
                progress?.Report($"正在下载 WebView2 运行时安装包… {percent}%");
            }
        }
    }
}
