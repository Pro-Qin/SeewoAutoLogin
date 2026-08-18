using System;
using System.Globalization;

namespace SeewoAutoLogin
{
    internal static class Strings
    {
        public static bool IsEnglish => CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        // For XAML binding
        public static StringsInstance Default { get; } = new StringsInstance();

        // App
        public static string AppTitle => IsEnglish ? "Seewo Auto Login" : "希沃自动登录";
        public static string AppDescription => IsEnglish ? "Standalone SSO gateway for Seewo Whiteboard" : "希沃白板 SSO 快捷登录独立服务";
        public static string GatewayStatus => IsEnglish ? "SSO Gateway running on port" : "SSO 网关运行于端口";
        public static string GatewayStopped => IsEnglish ? "SSO Gateway stopped" : "SSO 网关已停止";
        public static string Running => IsEnglish ? "Running" : "运行中";
        public static string Stopped => IsEnglish ? "Stopped" : "已停止";
        public static string ShowWindow => IsEnglish ? "Show Window" : "显示窗口";
        public static string HideWindow => IsEnglish ? "Hide Window" : "隐藏窗口";
        public static string Exit => IsEnglish ? "Exit" : "退出";
        public static string StartMinimized => IsEnglish ? "App started, running in system tray" : "应用已启动，正在系统托盘运行";
        public static string ConfirmExit => IsEnglish ? "Are you sure you want to exit? The SSO gateway will stop." : "确定退出吗？SSO 网关将停止，希沃白板将无法自动登录。";

        // Nav
        public static string NavAccounts => IsEnglish ? "Accounts" : "账号列表";
        public static string NavAddAccount => IsEnglish ? "Add Account" : "添加账号";
        public static string NavQrLogin => IsEnglish ? "QR Login" : "扫码登录";
        public static string NavSettings => IsEnglish ? "Settings" : "设置";
        public static string NavAbout => IsEnglish ? "About" : "关于";

        // Account List
        public static string AccountListTitle => IsEnglish ? "Account List" : "账号列表";
        public static string NoAccounts => IsEnglish ? "No accounts yet. Add one to get started." : "暂无账号，请添加账号。";
        public static string DeleteAccount => IsEnglish ? "Delete Account" : "删除账号";
        public static string DeleteConfirm(string name) => IsEnglish ? $"Delete account \"{name}\"?" : $"确定删除账号「{name}」吗？";
        public static string ActiveAccount => IsEnglish ? "Active" : "当前使用";
        public static string SetActive => IsEnglish ? "Set as Active" : "设为当前账号";
        public static string PasswordBacked => IsEnglish ? "Password Login" : "密码登录";
        public static string QrBacked => IsEnglish ? "QR Login" : "扫码登录";
        public static string LoggedInAs => IsEnglish ? "Logged in as" : "已登录为";

        // Add Account
        public static string AddAccountTitle => IsEnglish ? "Add Account" : "添加账号";
        public static string DisplayName => IsEnglish ? "Display Name" : "显示名称";
        public static string Username => IsEnglish ? "Phone / Username" : "手机号 / 用户名";
        public static string Password => IsEnglish ? "Password" : "密码";
        public static string LoginAndSave => IsEnglish ? "Login & Save" : "登录并保存";
        public static string LoggingIn => IsEnglish ? "Logging in..." : "登录中...";
        public static string LoginFailedNotice => IsEnglish ? "Login failed:" : "登录失败：";
        public static string LoginSuccessNotice => IsEnglish ? "Login successful!" : "登录成功！";
        public static string Cancel => IsEnglish ? "Cancel" : "取消";

        // QR Login
        public static string QrTitle => IsEnglish ? "Official Seewo QR Login" : "希沃官方扫码登录";
        public static string QrDescription => IsEnglish
            ? "Scan the QR code with the official Seewo mobile app to sign in."
            : "请使用希沃官方移动端扫描二维码登录。";
        public static string QrConsentTitle => IsEnglish ? "Authorize Seewo sign-in" : "授权希沃账号登录";
        public static string QrConsent => IsEnglish
            ? "Seewo Auto Login will contact id.seewo.com to create a QR session. Continue?"
            : "应用将连接 id.seewo.com 创建短时二维码会话。是否继续？";
        public static string Start => IsEnglish ? "Start QR Login" : "开始扫码登录";
        public static string Refresh => IsEnglish ? "Refresh QR Code" : "刷新二维码";
        public static string Creating => IsEnglish ? "Requesting QR code..." : "正在获取二维码…";
        public static string WaitingForScan => IsEnglish ? "Scan the QR code with the official Seewo mobile app." : "请使用希沃官方移动端扫描二维码。";
        public static string WaitingForConfirmation => IsEnglish ? "Scanned. Confirm on your phone." : "已扫码，请在手机上确认登录。";
        public static string Completing => IsEnglish ? "Verifying..." : "正在验证账号…";
        public static string Succeeded => IsEnglish ? "Signed in successfully." : "扫码登录成功。";
        public static string Expired => IsEnglish ? "QR code expired. Refresh and try again." : "二维码已过期，请刷新后重试。";
        public static string Cancelled => IsEnglish ? "QR login cancelled." : "已取消扫码登录。";
        public static string Denied => IsEnglish ? "Sign-in declined on phone." : "手机端已取消登录。";
        public static string NetworkError => IsEnglish
            ? "Cannot reach Seewo Account service.\nPlease check: network connection, proxy settings, firewall, or whether id.seewo.com / edu.seewo.com is accessible."
            : "无法连接希沃账号中心。\n请检查：网络连接、代理设置、防火墙，或 id.seewo.com / edu.seewo.com 是否可访问。";
        public static string ProtocolError => IsEnglish ? "Unexpected response from Seewo." : "希沃账号中心返回了无法识别的响应。";
        public static string Saved => IsEnglish ? "Account added." : "账号已添加。";
        public static string AccountFallback => IsEnglish ? "Seewo Account" : "希沃账号";
        public static string SecondsRemaining(int seconds) => IsEnglish ? $"{seconds}s remaining" : $"剩余 {seconds} 秒";
        public static string Continue => IsEnglish ? "Continue" : "继续";

        // Settings
        public static string SettingsTitle => IsEnglish ? "Settings" : "设置";
        public static string SecuritySettings => IsEnglish ? "Security Settings" : "安全设置";
        public static string UsePluginPassword => IsEnglish ? "Use password to protect settings" : "使用密码保护应用设置";
        public static string SetPassword => IsEnglish ? "Set Password" : "设置密码";
        public static string ClearPassword => IsEnglish ? "Clear Password" : "清除密码";
        public static string NewPassword => IsEnglish ? "New Password" : "新密码";
        public static string PasswordNotSet => IsEnglish ? "No password set" : "未设置密码";
        public static string PasswordSet => IsEnglish ? "Password set" : "已设置密码";
        public static string EnterPassword => IsEnglish ? "Enter password" : "请输入密码";
        public static string Unlock => IsEnglish ? "Unlock" : "解锁";
        public static string WrongPassword => IsEnglish ? "Wrong password" : "密码错误";

        // Rotation
        public static string RotationTitle => IsEnglish ? "User List Rotation" : "用户列表轮换";
        public static string RotationEnabled => IsEnglish ? "Enable user list rotation" : "启用用户列表轮换";
        public static string RotationGroupSize => IsEnglish ? "Users per group:" : "每组用户数：";
        public static string RotationHint => IsEnglish ? "Reopening within 10 seconds switches to the next group." : "窗口关闭后 10 秒内重新打开会切换到下一组。";

        // Auto Start
        public static string AutoStart => IsEnglish ? "Auto-start with Windows" : "开机自启";
        public static string MinimizeToTray => IsEnglish ? "Minimize to tray on close" : "关闭时最小化到托盘";

        // Gateway
        public static string GatewayInfo => IsEnglish ? "SSO Gateway is running. Seewo Whiteboard can now auto-login." : "SSO 网关运行中，希沃白板可以自动登录。";
        public static string GatewayPort => IsEnglish ? "Port:" : "端口：";
        public static string GatewayAccounts => IsEnglish ? "Accounts:" : "账号数：";

        // About
        public static string AboutTitle => IsEnglish ? "About" : "关于";
        public static string Version => IsEnglish ? "Version" : "版本";
        public static string OriginalAuthor => IsEnglish ? "Original Author: CJKmkp" : "原作者：CJKmkp";
        public static string ReconstructedAuthor => IsEnglish ? "Reconstructed by: Qin_zzq (LLM-assisted)" : "重构作者：Qin_zzq（LLM 辅助编程）";
        public static string ExtractNotice => IsEnglish ? "Extracted as standalone app from InkCanvas plugin" : "从 InkCanvas 插件提取为独立应用";
        public static string GithubLink => IsEnglish ? "GitHub Repository" : "GitHub 仓库";

        // Status
        public static string StatusRunning => IsEnglish ? "SSO Gateway is running" : "SSO 网关运行中";
        public static string StatusAccounts => IsEnglish ? "accounts configured" : "个账号已配置";
    }

    /// <summary>
    /// Instance wrapper for XAML binding (since static class can't be used in {Binding} directly)
    /// </summary>
    public class StringsInstance
    {
        public string AppTitle => Strings.AppTitle;
        public string AppDescription => Strings.AppDescription;
        public string NavAccounts => Strings.NavAccounts;
        public string NavAddAccount => Strings.NavAddAccount;
        public string NavQrLogin => Strings.NavQrLogin;
        public string NavSettings => Strings.NavSettings;
        public string NavAbout => Strings.NavAbout;
        public string AccountListTitle => Strings.AccountListTitle;
        public string NoAccounts => Strings.NoAccounts;
        public string DeleteAccount => Strings.DeleteAccount;
        public string ActiveAccount => Strings.ActiveAccount;
        public string SetActive => Strings.SetActive;
        public string AddAccountTitle => Strings.AddAccountTitle;
        public string DisplayName => Strings.DisplayName;
        public string Username => Strings.Username;
        public string Password => Strings.Password;
        public string LoginAndSave => Strings.LoginAndSave;
        public string LoggingIn => Strings.LoggingIn;
        public string LoginFailedNotice => Strings.LoginFailedNotice;
        public string Cancel => Strings.Cancel;
        public string QrTitle => Strings.QrTitle;
        public string QrDescription => Strings.QrDescription;
        public string QrConsentTitle => Strings.QrConsentTitle;
        public string QrConsent => Strings.QrConsent;
        public string Start => Strings.Start;
        public string Refresh => Strings.Refresh;
        public string SecuritySettings => Strings.SecuritySettings;
        public string UsePluginPassword => Strings.UsePluginPassword;
        public string SetPassword => Strings.SetPassword;
        public string ClearPassword => Strings.ClearPassword;
        public string NewPassword => Strings.NewPassword;
        public string PasswordNotSet => Strings.PasswordNotSet;
        public string PasswordSet => Strings.PasswordSet;
        public string EnterPassword => Strings.EnterPassword;
        public string Unlock => Strings.Unlock;
        public string WrongPassword => Strings.WrongPassword;
        public string RotationTitle => Strings.RotationTitle;
        public string RotationEnabled => Strings.RotationEnabled;
        public string RotationGroupSize => Strings.RotationGroupSize;
        public string RotationHint => Strings.RotationHint;
        public string AutoStart => Strings.AutoStart;
        public string MinimizeToTray => Strings.MinimizeToTray;
        public string AboutTitle => Strings.AboutTitle;
        public string Version => Strings.Version;
        public string OriginalAuthor => Strings.OriginalAuthor;
        public string ReconstructedAuthor => Strings.ReconstructedAuthor;
        public string ExtractNotice => Strings.ExtractNotice;
    }
}
