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
        public bool AutoStartEnabled { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; } = true;
        /// <summary>希沃打开但未登录时是否自动显示切换遮罩（全局控制，默认关闭）</summary>
        public bool AutoShowOverlay { get; set; }
    }

    public class SeewoAccount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string DisplayName { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string QrCredentialId { get; set; } = "";
        public DateTimeOffset? LastTokenExchangeAtUtc { get; set; }
        public SeewoUserInfo UserInfo { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        /// <summary>用户列表轮换/SSO 请求次数统计</summary>
        public int RequestCount { get; set; } = 0;
        /// <summary>最近一次 SSO 请求时间</summary>
        public DateTime? LastRequestAtUtc { get; set; }

        /// <summary>
        /// 获取解密后的密码（如果已加密则自动解密，否则返回原值）
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DecryptedPassword
        {
            get
            {
                if (string.IsNullOrEmpty(Password)) return "";
                if (Services.SecureStore.IsEncrypted(Password))
                {
                    try { return Services.SecureStore.Decrypt(Password); }
                    catch { return ""; } // 解密失败宁可登录失败，也不把密文当密码
                }
                return Password;
            }
        }
    }
}
