using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 配置文件的滚动备份与还原。每次配置内容变化都会归档一份（内容去重 + 60 秒节流），最多保留 20 份。
    /// </summary>
    internal static class ConfigBackupService
    {
        private const int MaxBackups = 20;
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
        }

        internal static string BackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "Backups");

        /// <summary>归档一份配置（内容未变化或距上次归档不足 60 秒时跳过）</summary>
        internal static void Archive(string json, bool force = false)
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
                    var name = "config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
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
                if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
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

        internal static string DescribeLatest()
        {
            var latest = List().FirstOrDefault();
            return latest == null ? "暂无备份" : latest.Time.ToString("MM-dd HH:mm");
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

        private static void Trim()
        {
            try
            {
                var files = new DirectoryInfo(BackupDir).GetFiles("config-*.json")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Skip(MaxBackups)
                    .ToList();
                foreach (var f in files)
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
