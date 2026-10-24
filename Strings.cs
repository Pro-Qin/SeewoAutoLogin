namespace SeewoAutoLogin
{
    internal static class Strings
    {
        public static bool IsEnglish => I18n.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        // For XAML binding
        public static StringsInstance Default { get; } = new StringsInstance();

        // App
        public static string AppTitle => "SeewoAutoLogin";
        public static string AppDescription => I18n.T("strings.AppDescription");
        public static string GatewayStatus => I18n.T("strings.GatewayStatus");
        public static string GatewayStopped => I18n.T("strings.GatewayStopped");
        public static string Running => I18n.T("strings.Running");
        public static string Stopped => I18n.T("strings.Stopped");
        public static string ShowWindow => I18n.T("strings.ShowWindow");
        public static string HideWindow => I18n.T("strings.HideWindow");
        public static string Exit => I18n.T("strings.Exit");
        public static string StartMinimized => I18n.T("strings.StartMinimized");
        public static string ConfirmExit => I18n.T("strings.ConfirmExit");

        // Nav
        public static string NavAccounts => I18n.T("strings.NavAccounts");
        public static string NavAddAccount => I18n.T("strings.NavAddAccount");
        public static string NavQrLogin => I18n.T("strings.NavQrLogin");
        public static string NavSettings => I18n.T("strings.NavSettings");
        public static string NavAbout => I18n.T("strings.NavAbout");

        // Account List
        public static string AccountListTitle => I18n.T("strings.AccountListTitle");
        public static string NoAccounts => I18n.T("strings.NoAccounts");
        public static string DeleteAccount => I18n.T("strings.DeleteAccount");
        public static string DeleteConfirm(string name) => I18n.T("strings.DeleteConfirm", name);
        public static string ActiveAccount => I18n.T("strings.ActiveAccount");
        public static string SetActive => I18n.T("strings.SetActive");
        public static string PasswordBacked => I18n.T("strings.PasswordBacked");
        public static string QrBacked => I18n.T("strings.QrBacked");
        public static string LoggedInAs => I18n.T("strings.LoggedInAs");

        // Add Account
        public static string AddAccountTitle => I18n.T("strings.AddAccountTitle");
        public static string DisplayName => I18n.T("strings.DisplayName");
        public static string Username => I18n.T("strings.Username");
        public static string Password => I18n.T("strings.Password");
        public static string LoginAndSave => I18n.T("strings.LoginAndSave");
        public static string LoggingIn => I18n.T("strings.LoggingIn");
        public static string LoginFailedNotice => I18n.T("strings.LoginFailedNotice");
        public static string LoginSuccessNotice => I18n.T("strings.LoginSuccessNotice");
        public static string Cancel => I18n.T("strings.Cancel");

        // QR Login
        public static string QrTitle => I18n.T("strings.QrTitle");
        public static string QrDescription => I18n.T("strings.QrDescription");
        public static string QrConsentTitle => I18n.T("strings.QrConsentTitle");
        public static string QrConsent => I18n.T("strings.QrConsent");
        public static string Start => I18n.T("strings.Start");
        public static string Refresh => I18n.T("strings.Refresh");
        public static string Creating => I18n.T("strings.Creating");
        public static string WaitingForScan => I18n.T("strings.WaitingForScan");
        public static string WaitingForConfirmation => I18n.T("strings.WaitingForConfirmation");
        public static string Completing => I18n.T("strings.Completing");
        public static string Succeeded => I18n.T("strings.Succeeded");
        public static string Expired => I18n.T("strings.Expired");
        public static string Cancelled => I18n.T("strings.Cancelled");
        public static string Denied => I18n.T("strings.Denied");
        public static string NetworkError => I18n.T("strings.NetworkError");
        public static string ProtocolError => I18n.T("strings.ProtocolError");
        public static string Saved => I18n.T("strings.Saved");
        public static string AccountFallback => I18n.T("strings.AccountFallback");
        public static string SecondsRemaining(int seconds) => I18n.T("strings.SecondsRemaining", seconds);
        public static string Continue => I18n.T("strings.Continue");

        // Settings
        public static string SettingsTitle => I18n.T("strings.SettingsTitle");
        public static string SecuritySettings => I18n.T("strings.SecuritySettings");
        public static string UsePluginPassword => I18n.T("strings.UsePluginPassword");
        public static string SetPassword => I18n.T("strings.SetPassword");
        public static string ClearPassword => I18n.T("strings.ClearPassword");
        public static string NewPassword => I18n.T("strings.NewPassword");
        public static string PasswordNotSet => I18n.T("strings.PasswordNotSet");
        public static string PasswordSet => I18n.T("strings.PasswordSet");
        public static string EnterPassword => I18n.T("strings.EnterPassword");
        public static string Unlock => I18n.T("strings.Unlock");
        public static string WrongPassword => I18n.T("strings.WrongPassword");

        // Rotation
        public static string RotationTitle => I18n.T("strings.RotationTitle");
        public static string RotationEnabled => I18n.T("strings.RotationEnabled");
        public static string RotationGroupSize => I18n.T("strings.RotationGroupSize");
        public static string RotationHint => I18n.T("strings.RotationHint");

        // Auto Start
        public static string AutoStart => I18n.T("strings.AutoStart");
        public static string MinimizeToTray => I18n.T("strings.MinimizeToTray");

        // Gateway
        public static string GatewayInfo => I18n.T("strings.GatewayInfo");
        public static string GatewayPort => I18n.T("strings.GatewayPort");
        public static string GatewayAccounts => I18n.T("strings.GatewayAccounts");

        // About
        public static string AboutTitle => I18n.T("strings.AboutTitle");
        public static string Version => I18n.T("strings.Version");
        public static string OriginalAuthor => I18n.T("strings.OriginalAuthor");
        public static string ReconstructedAuthor => I18n.T("strings.ReconstructedAuthor");
        public static string ExtractNotice => I18n.T("strings.ExtractNotice");
        public static string GithubLink => I18n.T("strings.GithubLink");

        // Status
        public static string StatusRunning => I18n.T("strings.StatusRunning");
        public static string StatusAccounts => I18n.T("strings.StatusAccounts");
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
