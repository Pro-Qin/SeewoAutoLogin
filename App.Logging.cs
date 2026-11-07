using System.Security.Principal;
using System.Windows;

namespace SeewoAutoLogin
{
    /// <summary>
    /// 日志等级。写进日志文件的标签（[INFO]/[WARN]/[ERROR]/[DEBUG]）与日志窗口的染色、
    /// 筛选都靠它。等级由调用点自己决定，不做关键词推断：「已清除失败计数」「重试成功」
    /// 这类句子含失败字样，自动推断只会造出一堆假警告。
    /// </summary>
    internal enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    public partial class App
    {
        #region Logging

        /// <summary>兼容既有调用点：不标等级的日志一律按 INFO 落盘。</summary>
        internal void WriteDiagnosticLog(string message) => WriteDiagnosticLog(LogLevel.Info, message);

        /// <summary>可自愈、但值得留痕的问题（探测失败、回退方案、复检未生效）。</summary>
        internal void WriteWarn(string message) => WriteDiagnosticLog(LogLevel.Warn, message);

        /// <summary>功能已经不可用、需要人介入的错误。</summary>
        internal void WriteError(string message) => WriteDiagnosticLog(LogLevel.Error, message);

        internal void WriteDiagnosticLog(LogLevel level, string message)
        {
            System.Diagnostics.Debug.WriteLine($"[SeewoAutoLogin][{LevelTag(level)}] {message}");
            try
            {
                var baseDir = Path.Combine(AppDataDir, "Logs");
                var path = Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelTag(level)}] {message}{Environment.NewLine}";
                lock (LogIoLock)
                {
                    Directory.CreateDirectory(baseDir);
                    RotateLogIfNeeded(path);
                    // FileShare.ReadWrite：网关/定时器/UI 多线程并发写日志时不再互相抛 IOException（丢日志）
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                    CleanupOldLogs(baseDir);
                }
            }
            catch { }
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };

        /// <summary>单个日志文件超过上限后滚动为 .1.log，避免长期驻留无限增长</summary>
        private static void RotateLogIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < LogRotationBytes) return;
                var rolled = path + ".1";
                if (File.Exists(rolled)) File.Delete(rolled);
                File.Move(path, rolled);
            }
            catch { }
        }

        /// <summary>每天最多清理一次：删除超过保留期的日志</summary>
        private static void CleanupOldLogs(string baseDir)
        {
            try
            {
                if (_lastLogCleanupDate == DateTime.Today) return;
                _lastLogCleanupDate = DateTime.Today;
                var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
                foreach (var file in new DirectoryInfo(baseDir).GetFiles("*.log*"))
                {
                    if (file.LastWriteTime < cutoff)
                    {
                        try { file.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        #endregion
    }
}
