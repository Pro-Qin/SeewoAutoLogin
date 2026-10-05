using System;
using Microsoft.Win32;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// Windows 7 上 HTTPS 出网的前置处理。
    ///
    /// 背景：.NET Framework 4.7 起 <c>ServicePointManager.SecurityProtocol</c> 默认是
    /// <c>SecurityProtocolType.SystemDefault</c>，实际用哪个协议由 Schannel 决定。微软的建议很明确
    /// ——不要硬编码协议版本（硬编码既屏蔽不了将来新增的协议，也不会让系统"多"出协议）。
    /// 所以要让 Win7 上的 HTTPS 真正跑通，唯一有效的动作是把**系统层**的 TLS 1.2 客户端打开。
    ///
    /// Windows 7 SP1 装上 KB3140245 后 Schannel 就支持 TLS 1.2，但**默认仍是关闭的**，
    /// 必须在 <c>SCHANNEL\Protocols\TLS 1.2\Client</c> 下显式写 Enabled=1（这正是微软 KB 里的做法）。
    /// 不打开的话，只接受 TLS 1.2+ 的服务端（希沃接口）会握手失败，
    /// 表现在界面上就是「添加账号失败 / 网络错误 / 登录信息过期」。
    ///
    /// 程序本来就以管理员运行（hosts 映射、SSO 网关、计划任务都需要），所以这里直接把注册表写好，
    /// 而不是丢一句"请自行修改系统设置"；写不进去才退回提示人工处理。
    /// </summary>
    internal static class NetworkCompat
    {
        private const string SchannelProtocolsPath =
            @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols";

        private const string SchannelTls12ClientPath = SchannelProtocolsPath + @"\TLS 1.2\Client";

        /// <summary>是否 Windows 7 / 8 / 8.1（6.1 / 6.2 / 6.3）。Win10+ 默认启用 TLS 1.2，不需要处理。</summary>
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
        /// 在 Win7/8.1 上确保 Schannel 启用了 TLS 1.2 客户端；未启用就写入注册表打开它。
        /// 只在这几个系统上动，Win10+ 直接跳过（本来就有，没必要碰注册表）。
        ///
        /// 调用时机要求已有管理员权限（写在 App 的提权判定之后）。
        /// </summary>
        public static void EnsureTls12ForLegacyWindows(Action<string> log)
        {
            if (!IsLegacyWindows) return;

            if (IsSchannelTls12ClientEnabled())
            {
                log?.Invoke("[网络] Schannel 已启用 TLS 1.2 客户端，HTTPS 出网无需额外处理");
                return;
            }

            try
            {
                using (var protocols = Registry.LocalMachine.CreateSubKey(SchannelProtocolsPath))
                using (var tls12 = protocols?.CreateSubKey("TLS 1.2"))
                using (var client = tls12?.CreateSubKey("Client"))
                {
                    if (client == null) throw new InvalidOperationException("无法创建 Schannel 注册表项");

                    client.SetValue("Enabled", 1, RegistryValueKind.DWord);
                    client.SetValue("DisabledByDefault", 0, RegistryValueKind.DWord);
                }

                // 注册表这一步只解决"没打开"。真正的 TLS 1.2 实现要靠 KB3140245，
                // 系统缺这个补丁时写注册表也没用，所以日志里把两种情况都点明。
                log?.Invoke("[网络] 检测到本机（Windows 7/8.1）未启用 Schannel 的 TLS 1.2 客户端，已自动写入注册表打开。"
                            + "重启系统后对本机所有程序生效；若重启后希沃接口仍连不上，说明系统缺少 KB3140245，请先安装该更新。");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[网络] 自动启用 TLS 1.2 失败（{ex.Message}）。请以管理员身份处理：先装 KB3140245，"
                            + $"再把 {SchannelTls12ClientPath} 下的 Enabled 设为 1（DWORD）、DisabledByDefault 设为 0，然后重启系统。");
            }
        }
    }
}
