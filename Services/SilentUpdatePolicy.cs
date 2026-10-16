namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 「后台下载要不要搬到前台显示」的判定。
    ///
    /// 用户手动点「下载新版本」时，后台可能正拿着同一个包在下载。两条路可选：
    /// 接过它的进度继续显示，或者掐断它、由前台重下一遍。判据只有两个 —— 瞬时速率，
    /// 以及已经下到的比例：一个快下完的包重新拉一遍，浪费的是教室的网络。
    /// </summary>
    internal static class SilentUpdatePolicy
    {
        /// <summary>快到这个速率就值得让用户看着它跑完（1 MB/s）。</summary>
        public const double FastBytesPerSecond = 1024.0 * 1024.0;

        /// <summary>过了一半之后，速率只要还过得去就不再重下（0.5 MB/s）。</summary>
        public const double HalfwayBytesPerSecond = 512.0 * 1024.0;

        /// <summary>「过半」的门槛。</summary>
        public const double HalfwayPercent = 50.0;

        /// <summary>接手后台下载：速率够快，或者已经过半且速率还过得去。</summary>
        public static bool ShouldAdopt(long bytesPerSecond, double? percent)
        {
            if (bytesPerSecond >= FastBytesPerSecond) return true;
            return (percent ?? 0) > HalfwayPercent && bytesPerSecond >= HalfwayBytesPerSecond;
        }

        /// <summary>给日志用的一句话：现在多快、下到哪儿了。</summary>
        public static string Describe(double bytesPerSecond, double? percent)
        {
            var where = percent.HasValue ? percent.Value.ToString("F0") + "%" : "进度未知";
            return FormatRate(bytesPerSecond) + "，" + where;
        }

        public static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1024.0 * 1024.0) return (bytesPerSecond / 1024.0 / 1024.0).ToString("F1") + " MB/s";
            if (bytesPerSecond >= 1024.0) return (bytesPerSecond / 1024.0).ToString("F0") + " KB/s";
            return bytesPerSecond.ToString("F0") + " B/s";
        }
    }
}
