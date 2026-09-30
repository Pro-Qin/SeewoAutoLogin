namespace SeewoAutoLogin.Services
{
    /// <summary>希沃白板版本与兼容状态对照，供日志与诊断包展示。</summary>
    internal static class CompatibilityMatrix
    {
        public static string Describe(string seewoVersion)
        {
            if (string.IsNullOrWhiteSpace(seewoVersion)) return "未知（未检测到希沃版本）";
            var v = seewoVersion.Trim();
            if (v.StartsWith("5.2", System.StringComparison.Ordinal)) return "已验证（5.2.x 开发环境）";
            if (v.StartsWith("5.", System.StringComparison.Ordinal)) return "待验证（建议先导出诊断包确认接口）";
            if (v.StartsWith("6.", System.StringComparison.Ordinal)) return "需适配验证（接口可能变化）";
            return "未记录（遇到问题请反馈版本号）";
        }
    }
}