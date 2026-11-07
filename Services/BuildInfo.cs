using System.Globalization;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 版本发布时间。
    ///
    /// 来源是编译期写进程序集元数据的 BuildDate（见 csproj 的 AssemblyMetadata），
    /// 读不到再退回可执行文件的写入时间。不查 GitHub 的 release 时间：
    /// 关于页要说的是「你手上这个版本是什么时候出来的」，离线、内网部署、
    /// 甚至私有构建都得答得出来，为一行文案去发网络请求也不划算。
    /// </summary>
    internal static class BuildInfo
    {
        private const string MetadataKey = "BuildDate";

        private static readonly Lazy<DateTime?> CachedBuildTime = new(ReadBuildTime, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>构建时间（本地时区）。完全取不到时为 null，此时关于页只显示版本号。</summary>
        internal static DateTime? BuildTime => CachedBuildTime.Value;

        /// <summary>「今天 / 3 天前 / 10月24日」——给用户看的发布时间。</summary>
        internal static string DescribeBuildAge()
        {
            var time = BuildTime;
            if (time == null) return "";
            var days = (DateTime.Today - time.Value.Date).Days;
            // 系统时间被往回调过时 days 会是负数：按「今天」处理，不显示「-2 天前」这种怪东西
            if (days <= 0) return "今天";
            if (days < 7) return days + " 天前";
            return time.Value.Year == DateTime.Today.Year
                ? time.Value.ToString("M月d日", CultureInfo.InvariantCulture)
                : time.Value.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        }

        /// <summary>完整日期，用于鼠标悬停时补充说明。</summary>
        internal static string DescribeBuildDate() =>
            BuildTime?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

        private static DateTime? ReadBuildTime()
        {
            try
            {
                var attribute = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.OrdinalIgnoreCase));
                if (attribute != null && TryParseStamp(attribute.Value, out var stamp))
                    return stamp.ToLocalTime();
            }
            catch { }

            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) exe = Assembly.GetExecutingAssembly().Location;
                // 单文件发布时 exe 的写入时间就是打包时刻；开发机上则是最近一次编译时间
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return File.GetLastWriteTime(exe);
            }
            catch { }

            return null;
        }

        /// <summary>
        /// csproj 里为了避开 MSBuild 属性函数里的冒号，写成 yyyyMMddTHHmmss（UTC）；
        /// 手工塞进去的 ISO 时间也要认，两种格式都试。
        /// </summary>
        private static bool TryParseStamp(string value, out DateTime stamp)
        {
            stamp = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();

            if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out stamp))
                return true;

            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out stamp);
        }
    }
}
