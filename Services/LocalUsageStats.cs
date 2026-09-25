using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 本机功能使用统计。
    ///
    /// 用途：了解哪些功能真的被用到、哪一步经常失败。
    /// 数据**只写在本机**，不联网、不上传；只有用户主动导出诊断包时才随包带出。
    /// （这是「不做遥测」与「想知道使用情况」之间的折中：拿到的是用户愿意给的那一份。）
    /// </summary>
    internal sealed class LocalUsageStats
    {
        private const int MaxKeys = 200;
        private readonly string _path;
        private readonly object _gate = new();
        private Dictionary<string, int> _counters = new();
        private DateTimeOffset _firstSeen = DateTimeOffset.UtcNow;

        private sealed class Snapshot
        {
            public Dictionary<string, int> Counters { get; set; } = new();
            public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
        }

        public LocalUsageStats(string dataDirectory)
        {
            _path = Path.Combine(dataDirectory, "usage-stats.json");
            Load();
        }

        /// <summary>给某个功能计数。key 用短横线命名，例如 rescan-account、csv-import。</summary>
        public void Bump(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            try
            {
                lock (_gate)
                {
                    _counters.TryGetValue(key, out var n);
                    _counters[key] = n + 1;
                    // 兜底：只保留计数最高的若干项，避免文件无限增长
                    if (_counters.Count > MaxKeys)
                    {
                        _counters = _counters.OrderByDescending(kv => kv.Value)
                            .Take(MaxKeys)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                    }
                }
                Save();
            }
            catch
            {
            }
        }

        /// <summary>取一份快照，供诊断包写入</summary>
        public (DateTimeOffset FirstSeenUtc, Dictionary<string, int> Counters) GetSnapshot()
        {
            lock (_gate) return (_firstSeen, new Dictionary<string, int>(_counters));
        }

        /// <summary>生成给人看的说明文本</summary>
        public string Describe()
        {
            var (first, counters) = GetSnapshot();
            if (counters.Count == 0) return "本机尚无功能使用记录。";

            var lines = new List<string>
            {
                "统计起始：" + first.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                "说明：以下计数只保存在本机，未上传。",
                "",
            };
            foreach (var kv in counters.OrderByDescending(kv => kv.Value))
                lines.Add($"  {kv.Key,-28} {kv.Value}");
            return string.Join(Environment.NewLine, lines);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path));
                if (snap?.Counters != null) _counters = snap.Counters;
                if (snap != null && snap.FirstSeenUtc != default) _firstSeen = snap.FirstSeenUtc;
            }
            catch
            {
                _counters = new Dictionary<string, int>();
            }
        }

        private void Save()
        {
            try
            {
                Snapshot snap;
                lock (_gate)
                    snap = new Snapshot { Counters = new Dictionary<string, int>(_counters), FirstSeenUtc = _firstSeen };
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }
}