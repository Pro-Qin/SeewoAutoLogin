namespace SeewoAutoLogin.Services
{
    /// <summary>一条登录记录。字段都用字符串，方便人直接看 jsonl 文件。</summary>
    internal sealed class LoginRecord
    {
        /// <summary>UTC 时间（ISO 8601）。</summary>
        public string AtUtc { get; set; } = "";

        public string AccountId { get; set; } = "";

        /// <summary>当时的展示名快照：账号后来被删掉，记录里也还看得懂是谁。</summary>
        public string Name { get; set; } = "";

        public string Username { get; set; } = "";

        /// <summary>来源：seewo（希沃请求）/ keepalive（自动续期）/ qr（扫码登录）/ password（密码登录）</summary>
        public string Source { get; set; } = "";

        public bool Success { get; set; }

        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// 登录记录：追加写 jsonl，供界面按账号查询「什么时候登录过、是谁触发的、成没成」。
    ///
    /// 单独一个文件而不是塞进 config.json：记录会一直增长，而 config 是整份重写的。
    /// 写入按「账号 + 来源」做 10 分钟节流 —— 希沃每 2 秒就会请求一次 SSOLOGIN，
    /// 不去重的话一天能写几万条，把「登录记录」变成垃圾日志。
    /// </summary>
    internal static class LoginHistoryService
    {
        private const int MaxRecords = 3000;
        private const int TrimTo = 2000;

        /// <summary>同一账号同一来源在这个间隔内只记一条。</summary>
        private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(10);

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DateTime> LastWrite = new Dictionary<string, DateTime>();
        private static int _sinceTrim;

        private static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeewoAutoLogin");

        /// <summary>仅供测试：把记录文件指到别处，避免单元测试往真实数据目录里写。</summary>
        internal static string FilePathOverride { get; set; }

        public static string FilePath => string.IsNullOrEmpty(FilePathOverride)
            ? Path.Combine(DataDir, "login-history.jsonl")
            : FilePathOverride;

        public static void Record(string accountId, string name, string username, string source, bool success, string detail)
        {
            try
            {
                var key = (accountId ?? "") + "|" + (source ?? "");
                var now = DateTime.UtcNow;
                lock (Gate)
                {
                    // 失败一定记（那是用户最需要看到的），成功才节流
                    if (success && LastWrite.TryGetValue(key, out var last) && now - last < Throttle) return;

                    Directory.CreateDirectory(DataDir);
                    var record = new LoginRecord
                    {
                        AtUtc = now.ToString("O"),
                        AccountId = accountId ?? "",
                        Name = name ?? "",
                        Username = username ?? "",
                        Source = source ?? "",
                        Success = success,
                        Detail = detail ?? ""
                    };
                    File.AppendAllText(FilePath, JsonSerializer.Serialize(record) + "\n", new UTF8Encoding(false));
                    LastWrite[key] = now;
                    _sinceTrim++;

                    if (_sinceTrim >= 50)
                    {
                        _sinceTrim = 0;
                        TrimIfNeeded();
                    }
                }
            }
            catch
            {
                // 记录失败绝不能影响登录本身
            }
        }

        /// <summary>查最近的登录记录，accountId 为空表示全部。返回按时间倒序。</summary>
        public static List<LoginRecord> Query(string accountId, int limit)
        {
            var result = new List<LoginRecord>();
            if (limit <= 0 || limit > 1000) limit = 200;
            try
            {
                if (!File.Exists(FilePath)) return result;
                var wanted = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();

                lock (Gate)
                {
                    foreach (var line in File.ReadLines(FilePath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        LoginRecord record;
                        try { record = JsonSerializer.Deserialize<LoginRecord>(line); }
                        catch { continue; }   // 半行 / 手工改坏的行直接跳过
                        if (record == null) continue;
                        if (wanted != null && !string.Equals(record.AccountId, wanted, StringComparison.Ordinal)) continue;
                        result.Add(record);
                    }
                }
            }
            catch
            {
                return result;
            }

            return result.OrderByDescending(r => r.AtUtc, StringComparer.Ordinal).Take(limit).ToList();
        }

        public static void Clear()
        {
            try
            {
                lock (Gate)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    LastWrite.Clear();
                    _sinceTrim = 0;
                }
            }
            catch { }
        }

        /// <summary>文件超过上限时保留最后一段，避免无限增长。</summary>
        private static void TrimIfNeeded()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (lines.Count <= MaxRecords) return;

                var kept = lines.Skip(lines.Count - TrimTo).ToList();
                File.WriteAllLines(FilePath, kept, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>给界面用的来源文案。</summary>
        public static string DescribeSource(string source)
        {
            switch (source)
            {
                case "seewo": return "希沃请求";
                case "keepalive": return "自动续期";
                case "qr": return "扫码登录";
                case "password": return "密码登录";
                default: return string.IsNullOrWhiteSpace(source) ? "未知" : source;
            }
        }
    }
}
