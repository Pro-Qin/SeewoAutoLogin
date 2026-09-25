using System;
using System.Collections.Generic;

namespace SeewoAutoLogin
{
    public class PluginConfig
    {
        /// <summary>希沃快捷登录窗口展示的账号数量上限（生效区大小）</summary>
        public const int MaxVisibleAccounts = 6;

        public bool IsFirstLaunch { get; set; } = true;
        public bool PasswordEncrypted { get; set; }
        public bool UserListRotationEnabled { get; set; }
        public int UserListRotationGroupSize { get; set; } = 6;
        public List<SeewoAccount> Accounts { get; set; } = new List<SeewoAccount>();
        public string ActiveAccountId { get; set; } = "";
        public bool UsePluginPassword { get; set; }
        public string PluginPasswordHash { get; set; } = "";
        public string PluginPasswordSalt { get; set; } = "";
        // 默认关闭：开机自启必须由用户在欢迎界面或设置里明确选择。
        // 若默认 true，App 启动时的「自启自愈」逻辑会替用户把启动项写进系统。
        public bool AutoStartEnabled { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; } = true;
        /// <summary>希沃打开但未登录时是否自动显示切换遮罩（全局控制，默认关闭）</summary>
        public bool AutoShowOverlay { get; set; }
        /// <summary>启动时自动检查更新（默认开启）</summary>
        public bool AutoCheckUpdate { get; set; } = true;
        /// <summary>
        /// 退出时恢复 hosts：删除本程序写入的 local.id.seewo.com 到 127.0.0.1 映射（默认开启）。
        /// 避免程序不再运行或被直接删除后 hosts 残留，导致希沃 SSO 请求一直指向本机。
        /// 下次启动会自动重新写入；写 hosts 需要管理员权限，失败只记录日志。
        /// </summary>
        public bool RestoreHostsOnExit { get; set; } = true;
        /// <summary>
        /// 更新包下载完成且 SHA256 校验通过后，自动静默安装（默认开启）。
        /// 关闭时只下载安装包并打开安装程序，由用户手动完成安装向导。
        /// 注意：旧版配置里没有这个字段，反序列化时会保留这里的默认值 true。
        /// </summary>
        public bool AutoInstallAfterDownload { get; set; } = true;
        /// <summary>自定义更新源（留空使用内置 GitHub API + jsDelivr 兜底）</summary>
        public string UpdateSource { get; set; } = "";
        /// <summary>本地 SSO 网关端口（24300 被占用时自动切换并记录在此）</summary>
        public int SsoGatewayPort { get; set; } = 24300;
        /// <summary>是否已看过使用教程（看过之后不再自动播放，仍可在设置里重看）</summary>
        public bool TourCompleted { get; set; }
        /// <summary>下次打开主界面时自动播放教程（在欢迎界面选择「查看教程」后置位）</summary>
        public bool PendingTour { get; set; }
    }

    public class SeewoAccount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string DisplayName { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string QrCredentialId { get; set; } = "";
        /// <summary>
        /// 占位账号：由「添加假账号」生成的演示数据，其凭据在希沃侧并不存在。
        /// 这类账号不参与后台保活与健康巡检 —— 否则只会持续失败，还会反复提示用户去处理。
        /// </summary>
        public bool IsPlaceholder { get; set; }
        public DateTimeOffset? LastTokenExchangeAtUtc { get; set; }
        public SeewoUserInfo UserInfo { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        /// <summary>用户列表轮换/SSO 请求次数统计</summary>
        public int RequestCount { get; set; } = 0;
        /// <summary>最近一次 SSO 请求时间</summary>
        public DateTime? LastRequestAtUtc { get; set; }
        /// <summary>
        /// 健康巡检结果：ok / warn / bad / unknown（空表示尚未巡检）。
        /// warn = 临时性故障（网络 / 服务端），后台会自动退避重试，不需要用户改密码；
        /// bad = 需要用户处理的凭据问题（密码已改 / 扫码令牌失效）。
        /// </summary>
        public string HealthState { get; set; } = "";
        /// <summary>健康巡检说明（失败原因等）</summary>
        public string HealthMessage { get; set; } = "";
        /// <summary>最近一次健康巡检时间</summary>
        public DateTime? LastHealthCheckAtUtc { get; set; }
        /// <summary>
        /// 临时性故障（网络 / 服务端）的下次重试时间（UTC）。
        /// 到点前后台保活会跳过该账号，避免每 5 分钟打一次接口、也避免把网络抖动反复放大。
        /// </summary>
        public DateTimeOffset? NextRetryAtUtc { get; set; }
        /// <summary>连续临时性故障次数，用于指数退避；续期成功后清零。</summary>
        public int TransientFailureCount { get; set; }

        /// <summary>凭据解密失败时的诊断出口（由 App 注入，便于在日志中显式记录而不是静默失败）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal static Action<string> DiagnosticSink { get; set; }

        /// <summary>最近一次解密是否失败（失败时界面应提示“凭据已失效，请重新录入”）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool PasswordDecryptFailed { get; private set; }

        /// <summary>
        /// 获取解密后的密码（如果已加密则自动解密，否则返回原值）。
        /// 解密失败时返回空串并置位 PasswordDecryptFailed —— 调用方必须据此提示“凭据已失效”，
        /// 不能把它当成“密码错误”，否则用户会误以为密码记错了。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DecryptedPassword
        {
            get
            {
                if (string.IsNullOrEmpty(Password)) return "";
                if (Services.SecureStore.IsEncrypted(Password))
                {
                    try
                    {
                        var plain = Services.SecureStore.Decrypt(Password);
                        PasswordDecryptFailed = false;
                        return plain;
                    }
                    catch (Exception ex)
                    {
                        PasswordDecryptFailed = true;
                        try { DiagnosticSink?.Invoke($"[Credential] 账号凭据解密失败（不是密码错误）; account-id={Id}; error={ex.GetType().Name} - {ex.Message}"); } catch { }
                        return "";
                    }
                }
                return Password;
            }
        }
    }
}
