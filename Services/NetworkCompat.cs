using System;
using Microsoft.Win32;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// Windows 7 上 HTTPS 出网的前置检查。
    ///
    /// 背景：.NET Framework 4.7 起 <c>ServicePointManager.SecurityProtocol</c> 默认是
    /// <c>SecurityProtocolType.SystemDefault</c>，实际用哪个协议由 Schannel 决定。微软的建议很明确
    /// ——不要硬编码协议版本（硬编码会屏蔽以后新增的协议，也会在低版本系统上强行降级）。
    ///
    /// 但 Windows 7 SP1 的 Schannel 默认**不开** TLS 1.2 客户端，而希沃接口这类现代服务端只接受 TLS 1.2+，
    /// 于是 Win7 上的表现就是「添加账号失败 / 网络错误 / 登录信息过期」。
    ///
    /// 因此这里只做**检测与记录**，不擅自修改系统安全配置：把结论写进诊断日志，
    /// 并给出一份可以直接照做的修复步骤（KB3140245 + 注册表开关）。
    /// </summary>
    internal static class NetworkCompat
    {
        private const string SchannelTls12ClientPath =
            @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client";

        /// <summary>是否 Windows 7 / 8 / 8.1（6.1 / 6.2 / 6.3）。Win10+ 默认启用 TLS 1.2，无需检查。</summary>
        private static bool IsLegacyWindows
        {
            get
            {
                var version = Environment.OSVersion.Version;
                return version.Major == 6 && version.Minor <= 3;
            }
        }

        /// <summary>
        /// Schannel 是否显式启用了 TLS 1.2 客户端。
        /// 微软要求的是 Enabled=1（DWORD）且 DisabledByDefault=0；键不存在即视为未启用。
        /// </summary>
        public static bool IsSchannelTls12ClientEnabled()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(SchannelTls12ClientPath))
                {
                    if (key == null) return false;

                    var enabled = key.GetValue("Enabled");
                    if (enabled == null || Convert.ToInt32(enabled) != 1) return false;

                    var disabledByDefault = key.GetValue("DisabledByDefault");
                    return disabledByDefault == null || Convert.ToInt32(disabledByDefault) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把 TLS 1.2 可用性写进诊断日志（仅在 Win7/8.1 上判断，避免在 Win10+ 上误报）。
        /// 不改系统配置，只让「添加账号失败」这类现象有据可查。
        /// </summary>
        public static void ReportTls12Availability(Action<string> log)
        {
            if (log == null || !IsLegacyWindows) return;

            if (IsSchannelTls12ClientEnabled())
            {
                log("[网络] Schannel 已启用 TLS 1.2 客户端，HTTPS 出网应可正常握手");
                return;
            }

            log("[网络] 警告：本机（Windows 7/8.1）未启用 Schannel 的 TLS 1.2 客户端。"
                + "希沃接口与更新检查只接受 TLS 1.2+，症状是「添加账号失败 / 网络错误」。"
                + "修复：先装 KB3140245，再把 " + SchannelTls12ClientPath
                + " 下的 Enabled 设为 1（DWORD）、DisabledByDefault 设为 0，然后重启系统。");
        }
    }
}
