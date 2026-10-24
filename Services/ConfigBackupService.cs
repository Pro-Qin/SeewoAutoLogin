using System.Security.Cryptography;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 配置文件的滚动备份与还原。
    ///
    /// 什么时候备份：配置内容变化时归档一份（内容去重 + 60 秒节流）。
    /// 保留多少：最多 12 份、且只留 30 天内的 —— 备份是拿来救急的，堆成几十份只会让
    /// 「查看备份」变成一屏噪音，真正需要的那一刻反而找不到。
    /// 每份备份带一个来源标记（account / settings / manual / import / update），
    /// 界面上直接告诉用户「这份是因为加了账号还是改设置产生的」。
    /// </summary>
    internal static class ConfigBackupService
    {
        private const int MaxBackups = 12;
        private const int MaxAgeDays = 30;
        private const int MinIntervalSeconds = 60;

        private static readonly object Gate = new object();
        private static string _lastHash;
        private static DateTime _lastArchiveUtc = DateTime.MinValue;

        internal class BackupItem
        {
            public string Name { get; set; }
            public DateTime Time { get; set; }
            public int Accounts { get; set; }
            public long Size { get; set; }
            /// <summary>来源标记：account / settings / manual / import / update（未知时为空）</summary>
            public string Reason { get; set; } = "";
        }

        internal static string BackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "Backups");

        /// <summary>归档一份配置（内容未变化或距上次归档不足 60 秒时跳过，force 可强制）</summary>
        internal static void Archive(string json, string reason = null, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            lock (Gate)
            {
                try
                {
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                    if (!force)
                    {
                        if (hash == _lastHash) return;
                        if ((DateTime.UtcNow - _lastArchiveUtc).TotalSeconds < MinIntervalSeconds) return;
                    }

                    Directory.CreateDirectory(BackupDir);
                    var tag = NormalizeReason(reason);
                    var name = "config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                               + (tag.Length > 0 ? "-" + tag : "") + ".json";
                    var path = Path.Combine(BackupDir, name);
                    File.WriteAllText(path, json, new UTF8Encoding(false));

                    _lastHash = hash;
                    _lastArchiveUtc = DateTime.UtcNow;
                    Trim();
                }
                catch { }
            }
        }

        internal static List<BackupItem> List()
        {
            var items = new List<BackupItem>();
            try
            {
                if (!Directory.Exists(BackupDir)) return items;
                foreach (var file in new DirectoryInfo(BackupDir).GetFiles("config-*.json"))
                {
                    items.Add(new BackupItem
                    {
                        Name = file.Name,
                        Time = file.LastWriteTime,
                        Size = file.Length,
                        Reason = ParseReason(file.Name),
                        Accounts = CountAccounts(file.FullName)
                    });
                }
            }
            catch { }
            return items.OrderByDescending(i => i.Time).ToList();
        }

        /// <summary>读取指定备份内容；name 必须是备份目录下的纯文件名（防目录穿越）</summary>
        internal static bool TryRead(string name, out string json, out string error)
        {
            json = null;
            error = null;
            try
            {
                if (!IsSafeName(name))
                {
                    error = "非法的备份文件名";
                    return false;
                }
                var path = Path.Combine(BackupDir, name);
                if (!File.Exists(path))
                {
                    error = "备份不存在";
                    return false;
                }
                json = File.ReadAllText(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>删除一份备份（界面上的「清理单份」）</summary>
        internal static bool Delete(string name, out string error)
        {
            error = null;
            try
            {
                if (!IsSafeName(name))
                {
                    error = "非法的备份文件名";
                    return false;
                }
                var path = Path.Combine(BackupDir, name);
                if (!File.Exists(path))
                {
                    error = "备份不存在";
                    return false;
                }
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static string DescribeLatest()
        {
            var latest = List().FirstOrDefault();
            return latest == null ? "暂无备份" : latest.Time.ToString("MM-dd HH:mm");
        }

        private static bool IsSafeName(string name)
            => !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "";
            var chars = reason.Trim().ToLowerInvariant().Where(c => c >= 'a' && c <= 'z').ToArray();
            var tag = new string(chars);
            return tag.Length > 16 ? tag.Substring(0, 16) : tag;
        }

        private static string ParseReason(string fileName)
        {
            // config-yyyyMMdd-HHmmss[-reason].json
            var body = Path.GetFileNameWithoutExtension(fileName);
            var parts = body.Split('-');
            return parts.Length >= 4 ? parts[3] : "";
        }

        private static int CountAccounts(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var config = System.Text.Json.JsonSerializer.Deserialize<PluginConfig>(json);
                return config?.Accounts?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>先按年龄淘汰（30 天前），再按数量淘汰（只留最新 12 份）。</summary>
        private static void Trim()
        {
            try
            {
                var files = new DirectoryInfo(BackupDir).GetFiles("config-*.json")
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                var cutoff = DateTime.Now.AddDays(-MaxAgeDays);
                foreach (var f in files.Where(f => f.LastWriteTime < cutoff))
                {
                    try { f.Delete(); } catch { }
                }

                foreach (var f in files.Where(f => f.LastWriteTime >= cutoff).Skip(MaxBackups))
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
