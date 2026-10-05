using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 一键把所有日志整合成一份纯文本，直接放到桌面。
    ///
    /// 与「导出诊断包」的分工：诊断包是给开发者看的 zip（配置、自检、脱敏后的账号概览）；
    /// 这个是「用户能直接拖进微信/QQ 发出去」的一份 txt —— 排查问题时最省事的一步，
    /// 不用教他去 AppData 里翻目录。
    ///
    /// 内容 = 环境摘要 + 全部运行日志（按时间正序拼接） + 全部崩溃日志。
    /// 日志总量超过上限时只保留最近的部分，并在文件里写明漏掉了哪些，避免导出一个几十兆、
    /// 根本发不出去的文件。
    /// </summary>
    internal static class LogExportService
    {
        /// <summary>导出文件的总量上限，超出就只保留最近的日志。</summary>
        private const long MaxBytes = 8 * 1024 * 1024;

        private static readonly Regex PhonePattern = new Regex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

        /// <summary>导出到桌面，返回实际写入的文件路径。</summary>
        public static string ExportToDesktop(string appDataDir, string appVersion, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(appDataDir))
                throw new ArgumentException("数据目录为空", nameof(appDataDir));

            var logsDir = Path.Combine(appDataDir, "Logs");
            var crashDir = Path.Combine(appDataDir, "crashes");

            var sb = new StringBuilder();
            AppendHeader(sb, appDataDir, appVersion, logsDir, crashDir);

            var skipped = new List<string>();
            AppendSection(sb, "运行日志", EnumerateByTimeDesc(logsDir, "*.log"), skipped);
            sb.AppendLine();
            AppendSection(sb, "崩溃日志", EnumerateByTimeDesc(crashDir, "crash-*.log"), skipped);

            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"（日志总量超过 {MaxBytes / 1024 / 1024} MB，以下文件未包含：{string.Join("、", skipped)}）");
            }

            var fileName = $"SeewoAutoLogin-日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var path = Path.Combine(DesktopOrTemp(), fileName);
            // 带 BOM 的 UTF-8：Windows 记事本打开无 BOM 的 UTF-8 会显示乱码，而用户多半就是双击记事本看
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

            log?.Invoke($"[LogExport] 已导出日志到 {path}");
            return path;
        }

        private static void AppendHeader(StringBuilder sb, string appDataDir, string appVersion, string logsDir, string crashDir)
        {
            sb.AppendLine("==== SeewoAutoLogin 日志导出 ====");
            sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"程序版本: {appVersion}");
            sb.AppendLine($"系统: {Safe(() => Environment.OSVersion.VersionString)}（{(SafeBool(() => Environment.Is64BitOperatingSystem) ? "64 位" : "32 位")}）");
            sb.AppendLine($"管理员权限: {(IsAdministrator() ? "是" : "否")}");
            sb.AppendLine($"WebView2 运行时: {Safe(() => WebView2Runtime.GetInstalledVersion()) ?? "<未检测到>"}");
            sb.AppendLine($"Schannel TLS 1.2 客户端: {(IsTls12Enabled() ? "已启用" : "未启用")}");
            sb.AppendLine($"数据目录: {appDataDir}");
            sb.AppendLine($"日志目录: {logsDir}");
            sb.AppendLine($"崩溃目录: {crashDir}");
            sb.AppendLine();
        }

        /// <summary>按文件名倒序（日志名就是日期，等价于时间倒序），最新的排在前面。</summary>
        private static List<FileInfo> EnumerateByTimeDesc(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return new List<FileInfo>();
                return new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.Name).ToList();
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        private static void AppendSection(StringBuilder sb, string title, List<FileInfo> files, List<string> skipped)
        {
            sb.AppendLine($"==== {title}（共 {files.Count} 个文件）====");
            if (files.Count == 0)
            {
                sb.AppendLine("（无）");
                return;
            }

            var picked = new List<KeyValuePair<FileInfo, string>>();
            long total = 0;
            foreach (var file in files)
            {
                string text;
                try { text = ReadShared(file.FullName); }
                catch (Exception ex) { text = $"（读取失败：{ex.Message}）"; }

                var size = Encoding.UTF8.GetByteCount(text);
                // 至少保留最新的一个文件，剩下的按体积取舍
                if (picked.Count > 0 && total + size > MaxBytes)
                {
                    skipped.Add(file.Name);
                    continue;
                }
                total += size;
                picked.Add(new KeyValuePair<FileInfo, string>(file, text));
            }

            for (var i = picked.Count - 1; i >= 0; i--)   // 还原成时间正序，方便从头往下读
            {
                sb.AppendLine();
                sb.AppendLine($"---------- {picked[i].Key.Name} ----------");
                sb.AppendLine(Sanitize(picked[i].Value));
            }
        }

        /// <summary>以共享方式读取：程序正在写日志时也要能导出成功。</summary>
        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>手机号打码：日志里偶尔会带上，导出文件是要发给别人的。</summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            try { return PhonePattern.Replace(text, "1**********"); }
            catch { return text; }
        }

        private static string DesktopOrTemp()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop)) return desktop;
            }
            catch { }
            return Path.GetTempPath();
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool IsTls12Enabled()
        {
            try { return NetworkCompat.IsSchannelTls12ClientEnabled(); }
            catch { return false; }
        }

        private static string Safe(Func<string> getter)
        {
            try { return getter(); }
            catch { return ""; }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try { return getter(); }
            catch { return false; }
        }
    }
}
