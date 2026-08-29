using SeewoAutoLogin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SeewoAutoLogin
{
    public partial class App : Application
    {
        private readonly SeewoAuthService _authService;
        private readonly SeewoQrLoginClient _qrLoginClient;
        private readonly QrLoginCoordinator _qrLoginCoordinator;
        private readonly QrSessionStore _qrSessionStore;
        private readonly SeewoUserListRotationService _userListRotation;
        private readonly SeewoSsoGateway _gateway;
        private readonly TrayIconService _trayIcon;
        private PluginConfig _config = new PluginConfig();
        private Timer _dailyTokenRefreshTimer;
        private ManagementWindow _mainWindow;
        private SeewoOverlay _overlay;
        public SeewoOverlay CurrentOverlay => _overlay;
        private Mutex _instanceMutex;
        private bool _isExiting;

        /// <summary>
        /// True when the application is performing an intentional shutdown (not minimize-to-tray).
        /// </summary>
        internal bool IsExiting => _isExiting;

        public PluginConfig Config => _config;
        public SeewoAuthService AuthService => _authService;
        public QrLoginCoordinator QrLoginCoordinator => _qrLoginCoordinator;
        public QrSessionStore QrSessionStore => _qrSessionStore;
        public SeewoUserListRotationService UserListRotation => _userListRotation;
        public SeewoSsoGateway Gateway => _gateway;
        public TrayIconService TrayIcon => _trayIcon;

        public SeewoAccount ActiveAccount => _config.Accounts.FirstOrDefault(a => a.Id == _config.ActiveAccountId);

        public string UserListRotationStatus
        {
            get
            {
                if (_userListRotation == null) return "";
                var rotation = _userListRotation;
                var totalRequests = rotation.TotalRequests;
                if (totalRequests <= 1) return "";
                return $"第 {rotation.CurrentGroupIndex + 1} 组";
            }
        }

        public App()
        {
            _authService = new SeewoAuthService();
            _authService.DiagnosticMessage += WriteDiagnosticLog;
            _qrLoginClient = new SeewoQrLoginClient();
            _qrLoginCoordinator = new QrLoginCoordinator(_qrLoginClient);
            _qrSessionStore = new QrSessionStore();
            _userListRotation = new SeewoUserListRotationService();
            _qrLoginClient.LogMessage += WriteDiagnosticLog;
            _qrLoginCoordinator.LogMessage += WriteDiagnosticLog;

            _userListRotation.RotationChanged += (groupIndex, withinWindow) =>
                WriteDiagnosticLog($"[Rotation] SSO request -> group {groupIndex + 1}; within-10s={withinWindow}");

            _trayIcon = new TrayIconService(
                ShowMainWindow,
                () => _config.Accounts.Select(a => new AccountMenuItem
                {
                    Id = a.Id,
                    DisplayName = a.DisplayName ?? a.Username ?? "",
                    RequestCount = a.RequestCount,
                    LastRequestAtUtc = a.LastRequestAtUtc
                }).ToList(),
                SwitchToAccount);

            LoadConfig();

            _gateway = new SeewoSsoGateway(_authService, () => _config, TryRestoreQrSession,
                GetVisibleAccounts,
                account => { account.UserInfo = _authService.UserInfo; SaveConfig(); },
                OnQrTokenValidated, _userListRotation, SaveConfig);
            _gateway.LogMessage += msg => WriteDiagnosticLog(msg);
            _gateway.AccountsServed += ids =>
            {
                _trayIcon.UpdateVisibleAccounts(ids);
                // 网关服务了账号后，通知管理窗口刷新请求计数展示
                _mainWindow?.Dispatcher.BeginInvoke(new Action(() => _mainWindow.NotifyAccountsServed()));
            };

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        /// <summary>
        /// 显示错误消息（弹窗 + 日志）
        /// </summary>
        internal void NotifyError(string title, string message)
        {
            WriteDiagnosticLog($"[ERROR] {title}: {message}");
            try { MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        }

        /// <summary>
        /// 显示信息消息（弹窗 + 日志）
        /// </summary>
        internal void NotifyInfo(string title, string message)
        {
            WriteDiagnosticLog($"[INFO] {title}: {message}");
            try { MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information); } catch { }
        }

        private const string InstanceMutexName = "SeewoAutoLogin_InstanceMutex_2F3A1B";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 卸载清理：setup.iss 的 UninstallRun 会调用 --uninstall
            if (e.Args.Contains("--uninstall"))
            {
                try { CleanupForUninstall(); }
                catch (Exception ex) { WriteDiagnosticLog($"[Uninstall] 清理失败: {ex.Message}"); }
                Shutdown();
                return;
            }

            // 检测是否已有实例在运行（互斥锁）
            bool isFirstInstance;
            Mutex instanceMutex = null;
            try
            {
                instanceMutex = new Mutex(true, InstanceMutexName, out isFirstInstance);
            }
            catch { isFirstInstance = true; }

            if (!isFirstInstance)
            {
                try
                {
                    var result = MessageBox.Show(
                        "已有希沃自动登录正在运行。\n\n" +
                        "选择操作：\n" +
                        "  · 是   → 关闭旧实例，使用当前\n" +
                        "  · 否   → 重启旧实例\n" +
                        "  · 取消 → 关闭当前",
                        "希沃自动登录",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // 关闭旧实例
                        foreach (var proc in Process.GetProcessesByName("SeewoAutoLogin")
                            .Where(p => p.Id != Environment.ProcessId))
                        {
                            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                        }
                        // 重新获取互斥锁所有权
                        instanceMutex?.Dispose();
                        instanceMutex = new Mutex(true, InstanceMutexName, out isFirstInstance);
                        WriteDiagnosticLog("[Instance] 已关闭旧实例，继续启动");
                        // 当前进程成为新实例，继续运行
                    }
                    else if (result == MessageBoxResult.No)
                    {
                        // 重启旧实例：关闭旧进程，然后重启新进程，退出当前
                        foreach (var proc in Process.GetProcessesByName("SeewoAutoLogin")
                            .Where(p => p.Id != Environment.ProcessId))
                        {
                            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                        }
                        // 启动新进程替代
                        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = "--elevated",
                                UseShellExecute = true
                            });
                        }
                        _isExiting = true;
                        Shutdown();
                        return;
                    }
                    else
                    {
                        // 取消 → 关闭当前
                        _isExiting = true;
                        Shutdown();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Instance] 实例检测异常: {ex.Message}");
                }
            }

            // 保存互斥锁引用，确保在进程退出前不释放
            if (isFirstInstance && instanceMutex != null)
            {
                _instanceMutex = instanceMutex;
            }
            else
            {
                instanceMutex?.Dispose();
            }

            // 检测管理员权限，非管理员自动提权重启。
            // 提权是 SSO 快捷登录能被希沃识别的前提：hosts 映射（local.id.seewo.com → 127.0.0.1）
            // 与 HttpListener 的非 localhost 前缀 URL ACL 都需要管理员权限。
            // 因此 UAC 被拒时不再静默降级运行，而是明确告知后果并让用户选择重试或降级。
            if (!e.Args.Contains("--elevated"))
            {
                try
                {
                    if (!IsAdministrator())
                    {
                        WriteDiagnosticLog("[Elevate] 非管理员权限，请求提权重启");
                        if (TryElevate())
                        {
                            _isExiting = true;
                            Shutdown();
                            return;
                        }

                        // UAC 被拒：说明后果，给出重试/降级选择，不静默降级
                        WriteDiagnosticLog("[Elevate] 用户取消 UAC 或提权失败");
                        var choice = MessageBox.Show(
                            "希沃快捷登录需要管理员权限才能正常工作（写入 hosts 映射、监听本地 SSO 网关）。\n\n" +
                            "当前以普通权限运行将导致希沃白板无法识别到快捷登录/账号列表入口。\n\n" +
                            "是否重试以管理员身份运行？\n" +
                            "  · 是   → 重新请求管理员权限\n" +
                            "  · 否   → 仍以普通权限运行（不推荐，SSO 快捷登录可能失效）",
                            "需要管理员权限",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (choice == MessageBoxResult.Yes)
                        {
                            // 重试提权；若仍失败则退出（降级运行对 SSO 无意义）
                            TryElevate();
                            _isExiting = true;
                            Shutdown();
                            return;
                        }

                        // 用户选择降级运行：降级模式下 SSO 可能不可用，记录日志
                        WriteDiagnosticLog("[Elevate] 用户选择降级运行（SSO 快捷登录可能不可用）");
                    }
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Elevate] 权限检测失败，以当前权限继续运行: {ex.Message}");
                }
            }
            else
            {
                // 已带 --elevated：验证是否真的获得管理员权限（仅记录日志，不弹窗）
                if (!IsAdministrator())
                    WriteDiagnosticLog("[Elevate] 提权重启后仍未获得管理员权限，以降级模式运行");
            }

            // 注册全局异常处理器
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                NotifyError("未处理异常", $"发生未处理异常:\n{args.ExceptionObject}");

            DispatcherUnhandledException += (_, args) =>
            {
                WriteDiagnosticLog($"[FATAL] UI 线程异常: {args.Exception}");
                args.Handled = true;
            };

            // 初始化托盘图标
            try
            {
                _trayIcon.Initialize();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"托盘初始化失败: {ex.Message}");
            }

            // 首次启动显示欢迎界面
            if (_config.IsFirstLaunch)
            {
                try
                {
                    var welcome = new WelcomeWindow();
                    // ShowDialog 如果失败属于致命错误，让上层 catch 处理
                    bool? dialogResult = welcome.ShowDialog();

                    if (dialogResult == true && welcome.AgreementAccepted)
                    {
                        _config.IsFirstLaunch = false;
                        SaveConfig();
                        WriteDiagnosticLog("[FirstLaunch] 用户已同意协议，首次启动完成");
                    }
                    else
                    {
                        WriteDiagnosticLog("[FirstLaunch] 用户未同意协议，应用退出");
                        _isExiting = true;
                        Shutdown();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    NotifyError("欢迎窗口错误", $"欢迎界面加载失败，跳过首次引导:\n{ex.Message}");
                    _config.IsFirstLaunch = false;
                    SaveConfig();
                }
            }

            // 启动 SSO 网关
            try
            {
                _gateway.Start();
                StartDailyTokenRefresh();
                WriteDiagnosticLog("SSO 网关启动成功");
            }
            catch (Exception ex)
            {
                NotifyError("SSO 网关错误",
                    $"SSO 网关启动失败: {ex.Message}\n\n" +
                    $"希沃自动登录功能可能无法正常工作。\n" +
                    $"请检查端口 24300 是否被其他程序占用，\n" +
                    $"或以管理员权限重新运行此应用。");
            }

            // 显示主窗口（除非设置了启动隐藏或传了 --minimized）
            bool startMinimized = e.Args.Contains("--minimized") || _config.StartMinimized;
            if (startMinimized)
            {
                try { _trayIcon?.SetStatusText(Strings.StartMinimized); } catch { }
            }
            else
            {
                try
                {
                    ShowMainWindow();
                }
                catch (Exception ex)
                {
                    NotifyError("窗口错误", $"无法显示主窗口:\n{ex.Message}");
                }
            }
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                var window = new ManagementWindow();
                window.Closed += (s, ev) =>
                {
                    if (!_isExiting && _config.MinimizeToTray)
                    {
                        _mainWindow = null;
                    }
                };
                _mainWindow = window;
            }

            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.Activate();
                _mainWindow.WindowState = WindowState.Normal;
            }
        }

        public void HideMainWindow()
        {
            if (_mainWindow != null)
            {
                _mainWindow.Hide();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _dailyTokenRefreshTimer?.Dispose();
            _qrLoginCoordinator?.Dispose();
            _qrLoginClient?.Dispose();
            _gateway?.Dispose();
            _authService?.Dispose();
            _userListRotation?.Dispose();
            _trayIcon?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        #region Elevation

        /// <summary>当前是否具有管理员权限</summary>
        private static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 以管理员身份（runas）重启当前程序，参数追加 --elevated。
        /// 返回 true 表示已成功拉起提权进程（当前进程应随后退出）。
        /// </summary>
        private bool TryElevate()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    WriteDiagnosticLog("[Elevate] 无法获取可执行文件路径，跳过提权");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--elevated",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Elevate] 提权失败（用户取消或系统限制）: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Account Management

        public IReadOnlyList<SeewoAccount> GetVisibleAccounts() => _config.Accounts;

        public void AddQrAccount(SeewoAccount account, QrLoginOutcome outcome)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.Token))
                throw new ArgumentException("扫码登录结果无效。", nameof(outcome));

            var existing = FindMatchingAccount(account.UserInfo);
            var credentialId = existing?.QrCredentialId;
            if (string.IsNullOrWhiteSpace(credentialId))
                credentialId = _qrSessionStore.CreateCredentialId();

            _qrSessionStore.Save(credentialId, outcome.Token, DateTimeOffset.UtcNow);
            account.QrCredentialId = credentialId;

            if (existing != null)
            {
                existing.DisplayName = account.DisplayName;
                existing.Username = account.Username;
                existing.Password = "";
                existing.UserInfo = account.UserInfo;
                existing.QrCredentialId = credentialId;
                if (_config.ActiveAccountId == "") _config.ActiveAccountId = existing.Id;
                SaveConfig();
                WriteDiagnosticLog($"[Account] 已更新扫码账号会话; account-id={existing.Id}");
                return;
            }

            AddAccount(account);
        }

        public void AddAccount(SeewoAccount account)
        {
            _config.Accounts.Add(account);
            if (_config.Accounts.Count == 1)
                _config.ActiveAccountId = account.Id;
            SaveConfig();
            WriteDiagnosticLog($"[Account] 已保存账号; account-id={account.Id}");
        }

        public void RemoveAccount(string accountId)
        {
            var account = _config.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (!string.IsNullOrWhiteSpace(account?.QrCredentialId))
            {
                try { _qrSessionStore.Delete(account.QrCredentialId); }
                catch { }
            }
            _config.Accounts.RemoveAll(a => a.Id == accountId);
            if (_config.ActiveAccountId == accountId)
                _config.ActiveAccountId = _config.Accounts.FirstOrDefault()?.Id ?? "";
            SaveConfig();
        }

        public void SwitchActiveAccount(string accountId)
        {
            _config.ActiveAccountId = accountId;
            _authService.Logout();
            SaveConfig();
            WriteDiagnosticLog($"[Account] 切换到账号; account-id={accountId}");
        }

        /// <summary>切换遮罩层显示/隐藏（仅由托盘触发；遮罩自带 20s 自动关闭）</summary>
        public void ToggleOverlay()
        {
            if (_overlay != null && _overlay.IsVisible)
            {
                _overlay.Close();
                _overlay = null;
            }
            else
            {
                _overlay = new SeewoOverlay();
                _overlay.Closed += (_, _) => { if (_overlay != null && !_overlay.IsVisible) _overlay = null; };
                _overlay.Show();
            }
        }

        /// <summary>托盘右键菜单：将未展示的账号提到最前，或执行重启/退出</summary>
        public void SwitchToAccount(string accountId)
        {
            if (accountId == "__RESTART__") { RestartApp(); return; }
            if (accountId == "__EXIT__") { BeginExit(); return; }
            if (accountId == "__OVERLAY__") { ToggleOverlay(); return; }
            if (RequiresPasswordUnlock()) return;
            var account = _config.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return;
            _config.Accounts.Remove(account);
            _config.Accounts.Insert(0, account);
            WriteDiagnosticLog($"[Tray] 已将账号 {account.DisplayName ?? account.Username} 提到最前");
            SaveConfig();
            // 刷新托盘菜单
            _trayIcon.UpdateVisibleAccounts(new List<string>());
        }

        /// <summary>托盘切换账号属于配置变更：启用密码保护时要求先验证密码</summary>
        private bool RequiresPasswordUnlock()
        {
            if (!_config.UsePluginPassword || string.IsNullOrEmpty(_config.PluginPasswordHash))
                return false;
            try
            {
                var dlg = new TextInputDialog(Strings.AppTitle, Strings.EnterPassword)
                {
                    Owner = _mainWindow,
                    IsPassword = true
                };
                if (dlg.ShowDialog() != true) return true; // 取消 = 不执行
                return !VerifyPluginPassword(dlg.InputText, _config.PluginPasswordHash, _config.PluginPasswordSalt);
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Tray] 密码验证对话框异常: {ex.GetType().Name}");
                return false; // 弹窗失败时不阻塞托盘切换
            }
        }

        private static bool VerifyPluginPassword(string password, string expectedHash, string salt)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(expectedHash)) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password + salt)));
            return hash == expectedHash;
        }

        private SeewoAccount FindMatchingAccount(SeewoUserInfo userInfo)
        {
            if (userInfo == null) return null;
            return _config.Accounts.FirstOrDefault(account =>
                (!string.IsNullOrWhiteSpace(userInfo.AccountId) &&
                 string.Equals(account.UserInfo?.AccountId, userInfo.AccountId, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(userInfo.UserName) &&
                 string.Equals(account.UserInfo?.UserName, userInfo.UserName, StringComparison.Ordinal)));
        }

        public void OnQrTokenValidated(SeewoAccount account, string token)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.QrCredentialId) || string.IsNullOrWhiteSpace(token))
                return;
            try
            {
                _qrSessionStore.Save(account.QrCredentialId, token, DateTimeOffset.UtcNow);
                WriteDiagnosticLog($"[Session] Token 换发返回新 Token; account-id={account.Id}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Session] 更新 DPAPI 凭据失败; account-id={account.Id}; error={ex.GetType().Name}");
            }
        }

        public bool TryRestoreQrSession(SeewoAccount account)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.QrCredentialId))
            {
                WriteDiagnosticLog($"[Session] 扫码账号没有可恢复的凭据; account-id={account?.Id ?? "<none>"}");
                return false;
            }

            if (!_qrSessionStore.TryLoad(account.QrCredentialId, out var session))
            {
                WriteDiagnosticLog($"[Session] 扫码凭据读取失败; account-id={account.Id}");
                return false;
            }

            _authService.RestoreQrSession(session.Token, account.UserInfo);
            var restored = _authService.IsSessionFor(account);
            WriteDiagnosticLog($"[Session] 扫码会话恢复; account-id={account.Id}; restored={restored}");
            return restored;
        }

        #endregion

        /// <summary>
        /// 重启应用（以管理员身份）
        /// </summary>
        internal void RestartApp()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--elevated",
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                }
            }
            catch { }
            _isExiting = true;
            Shutdown();
        }

        /// <summary>
        /// 退出应用
        /// </summary>
        internal void BeginExit()
        {
            var result = MessageBox.Show(
                Strings.ConfirmExit,
                Strings.AppTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _isExiting = true;
                _mainWindow?.Close();
                Shutdown();
            }
        }

        #region Token Refresh

        private void StartDailyTokenRefresh()
        {
            _dailyTokenRefreshTimer = new Timer(_ => _ = RefreshTokensAsync(), null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
        }

        private async Task RefreshTokensAsync()
        {
            foreach (var account in _config.Accounts
                .Where(account => string.IsNullOrEmpty(account.Password) && !string.IsNullOrWhiteSpace(account.QrCredentialId))
                .ToList())
            {
                try
                {
                    if (!TryRestoreQrSession(account)) continue;
                    var result = await _authService.ExchangeCurrentTokenAsync().ConfigureAwait(false);
                    if (!result.Success) continue;
                    OnQrTokenValidated(account, result.Token);
                    WriteDiagnosticLog($"[Scheduler] Token 自动刷新成功; account-id={account.Id}");
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Scheduler] Token 自动刷新失败; account-id={account.Id}; error={ex.GetType().Name}");
                }
            }
        }

        #endregion

        #region Config Persistence

        private string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "config.json");

        public void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    _config = new PluginConfig();
                    return;
                }
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded != null)
                {
                    loaded.Accounts ??= new System.Collections.Generic.List<SeewoAccount>();
                    _config = loaded;
                    // 启动时迁移旧配置：明文/旧格式密码统一转为带前缀的 DPAPI 密文
                    try
                    {
                        if (EnsurePasswordsEncrypted()) SaveConfig();
                    }
                    catch (Exception ex)
                    {
                        WriteDiagnosticLog($"[Config] 密码加密失败（配置未写入明文）: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"加载配置失败: {ex.Message}");
                _config = new PluginConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 加密明文/旧格式密码；加密失败会抛异常，由外层 catch 中止本次保存，绝不把明文写盘
                EnsurePasswordsEncrypted();

                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"保存配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将账号密码统一转为带前缀的 DPAPI 密文（兼容旧版无前缀密文与历史明文）。
        /// 加密失败抛出异常，由调用方中止保存，避免明文落盘。
        /// </summary>
        private bool EnsurePasswordsEncrypted()
        {
            bool changed = false;
            foreach (var acct in _config.Accounts)
            {
                if (string.IsNullOrEmpty(acct.Password)) continue;
                if (Services.SecureStore.IsEncrypted(acct.Password)) continue;

                // 旧版无前缀：可能是 DPAPI 密文，也可能是明文；先尝试解密
                string plaintext = Services.SecureStore.TryDecryptLegacy(acct.Password);
                if (plaintext == null) plaintext = acct.Password;

                acct.Password = Services.SecureStore.Encrypt(plaintext); // 失败抛异常，中止保存
                changed = true;
            }
            if (changed) _config.PasswordEncrypted = true;
            return changed;
        }

        /// <summary>
        /// 卸载清理：移除 hosts 中的 local.id.seewo.com 映射，并删除本应用数据目录。
        /// </summary>
        private void CleanupForUninstall()
        {
            try
            {
                var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "drivers", "etc", "hosts");
                if (File.Exists(hostsPath))
                {
                    var lines = File.ReadAllLines(hostsPath)
                        .Where(l => !l.Contains("local.id.seewo.com", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    File.WriteAllLines(hostsPath, lines);
                    WriteDiagnosticLog("[Uninstall] 已移除 hosts 中的 local.id.seewo.com 映射");
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 清理 hosts 失败（可能需要管理员权限）: {ex.Message}");
            }

            try
            {
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeewoAutoLogin");
                if (Directory.Exists(appData))
                {
                    Directory.Delete(appData, recursive: true);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 删除数据目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出配置到文件
        /// </summary>
        internal void ExportConfig(string filePath)
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                WriteDiagnosticLog($"[Config] 配置已导出到 {filePath}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Config] 导出失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 从文件导入配置
        /// </summary>
        internal bool ImportConfig(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded == null) return false;
                loaded.Accounts ??= new List<SeewoAccount>();
                _config = loaded;
                SaveConfig();
                WriteDiagnosticLog($"[Config] 已从 {filePath} 导入配置 ({_config.Accounts.Count} 个账号)");
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Config] 导入失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Logging

        internal void WriteDiagnosticLog(string message)
        {
            try
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeewoAutoLogin", "Logs");
                var path = Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}";
                Directory.CreateDirectory(baseDir);
                File.AppendAllText(path, line);
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"[SeewoAutoLogin] {message}");
        }

        #endregion
    }
}
