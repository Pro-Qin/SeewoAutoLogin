using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SeewoAutoLogin.Services;

namespace SeewoAutoLogin
{
    public partial class App
    {
        #region Logging

        internal void WriteDiagnosticLog(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[SeewoAutoLogin] {message}");
            try
            {
                var baseDir = Path.Combine(AppDataDir, "Logs");
                var path = Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}";
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
