using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 崩溃可观测性与安全模式。
    ///
    /// - 记录 AppDomain / Dispatcher / TaskScheduler 未处理异常到 %LOCALAPPDATA%\SeewoAutoLogin\crashes；
    /// - 记录启动尝试次数，连续 3 次启动失败后写入 safe-mode.flag；
    /// - 下次启动检测到安全模式标记时，跳过 WebView2 预热、遮罩与自动更新，只保留网关与保活。
    /// </summary>
    internal static class CrashReporter
    {
        private static readonly object Gate = new object();
        private static bool _installed;
        private static int _startupAttempts;

        private static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeewoAutoLogin");
        private static string CrashDir => Path.Combine(DataDir, "crashes");
        private static string SafeModeFlag => Path.Combine(DataDir, "safe-mode.flag");
        private static string AttemptFile => Path.Combine(DataDir, "startup-attempts.txt");

        /// <summary>是否应进入安全模式。</summary>
        public static bool IsSafeModeRequested
        {
            get
            {
                try { return File.Exists(SafeModeFlag) || _startupAttempts >= 3; }
                catch { return false; }
            }
        }

        /// <summary>注册全局异常处理；重复调用只生效一次。</summary>
        public static void Install(Application app, Action<string> log)
        {
            if (_installed) return;
            _installed = true;
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                    Report(e.ExceptionObject as Exception, "AppDomain", log);
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    Report(e.Exception, "TaskScheduler", log);
                    e.SetObserved();
                };
                if (app != null)
                {
                    app.DispatcherUnhandledException += (_, e) =>
                    {
                        Report(e.Exception, "Dispatcher", log);
                        e.Handled = true;
                    };
                }
            }
            catch { }
        }

        /// <summary>启动时记录一次尝试。连续失败达到阈值后进入安全模式。</summary>
        public static void MarkStartupAttempt(Action<string> log)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    _startupAttempts = ReadAttempts() + 1;
                    File.WriteAllText(AttemptFile, _startupAttempts.ToString());
                    log?.Invoke($"[Crash] 启动尝试次数: {_startupAttempts}");
                }
                catch { }
            }
        }

        /// <summary>启动 60 秒后仍然存活：清除失败计数与安全模式标记。</summary>
        public static void MarkStartupSuccess(Action<string> log)
        {
            lock (Gate)
            {
                try
                {
                    _startupAttempts = 0;
                    File.WriteAllText(AttemptFile, "0");
                    if (File.Exists(SafeModeFlag)) File.Delete(SafeModeFlag);
                    log?.Invoke("[Crash] 启动健康确认通过，已清除失败计数");
                }
                catch { }
            }
        }

        /// <summary>记录未处理异常；连续启动失败达到阈值时写入安全模式标记。</summary>
        public static void Report(Exception ex, string source, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(CrashDir);
                var file = Path.Combine(CrashDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                var sb = new StringBuilder();
                sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"来源: {source}");
                sb.AppendLine($"进程: {Environment.ProcessPath}");
                sb.AppendLine($"版本: {typeof(CrashReporter).Assembly.GetName().Version}");
                sb.AppendLine($"异常: {ex?.GetType().FullName}: {ex?.Message}");
                sb.AppendLine();
                sb.AppendLine(ex?.ToString());
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                log?.Invoke($"[Crash] 已记录未处理异常: {ex?.GetType().Name} - {ex?.Message}");

                if (_startupAttempts >= 3)
                {
                    File.WriteAllText(SafeModeFlag, DateTimeOffset.UtcNow.ToString("O"));
                    log?.Invoke("[Crash] 连续启动失败达到阈值，已写入安全模式标记");
                }
            }
            catch { }
        }

        private static int ReadAttempts()
        {
            try
            {
                if (!File.Exists(AttemptFile)) return 0;
                return int.TryParse(File.ReadAllText(AttemptFile).Trim(), out var value) ? value : 0;
            }
            catch { return 0; }
        }
    }
}