using System.Globalization;
using System.Runtime.InteropServices;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 供「疑难解答」页使用的环境摘要。
    ///
    /// 用户来提 issue 时，最耗时间的不是描述问题，而是来回问「你什么版本、什么系统、
    /// 网关起来没有」。这段文本把这些一次说清，且只含环境与计数，不含账号、密码、
    /// 手机号、日志原文 —— 可以放心贴到公开的 issue 里。
    /// </summary>
    internal static class SupportInfo
    {
        /// <summary>生成一段可直接粘贴进 issue 的 Markdown 环境摘要。</summary>
        internal static string BuildIssueReport(string appVersion, object selfCheck, IEnumerable<SeewoAccount> accounts)
        {
            var root = ToElement(selfCheck);
            var builder = new StringBuilder();

            builder.AppendLine("### 环境摘要");
            builder.AppendLine();
            builder.AppendLine("| 项目 | 值 |");
            builder.AppendLine("| --- | --- |");
            Row(builder, "程序版本", DescribeVersion(appVersion));
            Row(builder, "系统", DescribeOs());
            Row(builder, "运行时", ".NET " + Environment.Version.ToString(3) + " · " + RuntimeInformation.ProcessArchitecture);
            Row(builder, "WebView2", WebView2Runtime.GetInstalledVersion() ?? "未安装");
            Row(builder, "希沃白板", DescribeSeewo(root));
            Row(builder, "开机自启", DescribeAutoStart(root));
            Row(builder, "SSO 网关", DescribeGateway(root));
            Row(builder, "域名映射", Flag(root, "hostsOk") ? "已写入 hosts" : "缺失");
            Row(builder, "权限", Flag(root, "isAdmin") ? "管理员" : "普通用户");
            Row(builder, "账号", DescribeAccounts(accounts));

            builder.AppendLine();
            builder.AppendLine("> 摘要由程序生成于 " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "，不含账号与密码。");

            return builder.ToString();
        }

        private static string DescribeVersion(string appVersion)
        {
            var version = string.IsNullOrWhiteSpace(appVersion) ? "(未知)" : appVersion;
            var date = BuildInfo.DescribeBuildDate();
            return string.IsNullOrEmpty(date) ? version : version + "（" + date + " 构建）";
        }

        private static string DescribeOs()
        {
            var description = RuntimeInformation.OSDescription ?? "";
            // Windows 下 OSDescription 形如 "Microsoft Windows 10.0.26100"，去掉冗余前缀更好读
            description = description.Replace("Microsoft ", "").Trim();
            return string.IsNullOrEmpty(description) ? Environment.OSVersion.VersionString : description;
        }

        private static string DescribeSeewo(JsonElement root)
        {
            if (Flag(root, "seewoRunning")) return "运行中";
            var exe = SeewoLauncher.FindExecutable();
            return string.IsNullOrEmpty(exe) ? "未安装或未找到" : "已安装（未运行）";
        }

        private static string DescribeAutoStart(JsonElement root)
        {
            if (!Flag(root, "autoStartEnabled")) return "未启用";
            var mode = Text(root, "autoStartMode");
            return string.IsNullOrEmpty(mode) ? "已启用" : "已启用（" + mode + "）";
        }

        private static string DescribeGateway(JsonElement root)
        {
            if (!Flag(root, "gatewayRunning")) return "未运行";
            var port = Number(root, "gatewayPort");
            var expected = Number(root, "expectedPort");
            var text = "运行中 · 端口 " + port.ToString(CultureInfo.InvariantCulture);
            if (expected > 0 && port != expected) text += "（应为 " + expected.ToString(CultureInfo.InvariantCulture) + "）";
            return text;
        }

        private static string DescribeAccounts(IEnumerable<SeewoAccount> accounts)
        {
            if (accounts == null) return "0 个";

            var total = 0;
            var ok = 0;
            foreach (var account in accounts)
            {
                total++;
                if (string.Equals(account.HealthState, "ok", StringComparison.OrdinalIgnoreCase)) ok++;
            }

            return total == 0 ? "0 个" : total + " 个（正常 " + ok + " · 异常 " + (total - ok) + "）";
        }

        private static void Row(StringBuilder builder, string name, string value)
        {
            builder.Append("| ").Append(name).Append(" | ").Append(Sanitize(value)).AppendLine(" |");
        }

        /// <summary>表格里的竖线与换行会把 Markdown 结构冲散</summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// 自检状态是 App.BuildSelfCheckStatus 返回的匿名对象，类型拿不到也编译不出来；
        /// 走一次 JSON 序列化再读字段，比对 object 反射强。
        /// </summary>
        private static JsonElement ToElement(object selfCheck)
        {
            if (selfCheck == null) return default;
            try
            {
                var json = JsonSerializer.Serialize(selfCheck);
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            catch
            {
                return default;
            }
        }

        private static bool Flag(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object) return false;
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }

        private static string Text(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object) return "";
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : "";
        }

        private static int Number(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object) return 0;
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : 0;
        }
    }
}
