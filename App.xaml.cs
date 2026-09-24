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

        /// <summary>主界面开场动画是否已播放（每个进程只播放一次）</summary>
        internal bool IntroPlayed { get; set; }

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
            // 网络出口决策（系统代理/直连回退）也写进日志，便于排查“登录失败 / 登录信息过期”。
            NetworkRoute.DiagnosticMessage += WriteDiagnosticLog;
            SeewoAccount.DiagnosticSink = WriteDiagnosticLog;

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
            MigratePlaceholderFlags();

            _gateway = new SeewoSsoGateway(_authService, () => _config, TryRestoreQrSession,
                GetVisibleAccounts,
                account => { account.UserInfo = _authService.UserInfo; SaveConfig(); },
                OnQrTokenValidated, _userListRotation, SaveConfig);
            // 希沃固定请求 24300：始终以该端口为首选（配置里记录的值只用于诊断展示，不作为首选端口）
            _gateway.Port = SeewoSsoGateway.SeewoExpectedPort;
            _gateway.ConfirmStopEasiAgent = (pid, path) => Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"本地 SSO 网关端口被希沃 EasiAgent 占用（pid={pid}）。\n\n" +
                    "结束它可以立刻让快捷登录生效，但可能中断正在进行的希沃操作。\n\n" +
                    "  · 是   → 结束 EasiAgent 并使用该端口\n" +
                    "  · 否   → 不结束任何进程，自动改用备用端口（希沃可能仍请求原端口）",
                    Strings.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            _gateway.PortChanged += port =>
            {
                _config.SsoGatewayPort = port;
                SaveConfig();
            };
            _gateway.LogMessage += msg => WriteDiagnosticLog(msg);
            _gateway.AccountsServed += ids =>
            {
                // 该回调来自网关的 HTTP 线程：托盘菜单是 WinForms 控件（主窗口是 WPF），统一回到 UI 线程再更新
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { _trayIcon.UpdateVisibleAccounts(ids); } catch { }
                    try { _mainWindow?.NotifyAccountsServed(); } catch { }
                }));
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

        /// <summary>单实例唤醒事件：后启动的实例用它请求已在运行的实例显示主窗口</summary>
        private const string InstanceSignalName = "SeewoAutoLogin_ShowWindow_7B4C1E";

        private EventWaitHandle _instanceSignal;
        private CancellationTokenSource _instanceSignalCts;

        /// <summary>开始监听「请显示主窗口」的唤醒请求（仅第一个实例需要）</summary>
        private void StartInstanceSignalListener()
        {
            try
            {
                _instanceSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignalName);
                _instanceSignalCts = new CancellationTokenSource();
                var token = _instanceSignalCts.Token;

                Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (!_instanceSignal.WaitOne(TimeSpan.FromSeconds(1))) continue;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    WriteDiagnosticLog("[Instance] 收到唤醒请求，显示主窗口");
                                    ShowMainWindow();
                                }
                                catch (Exception ex) { WriteDiagnosticLog($"[Instance] 显示主窗口失败: {ex.Message}"); }
                            }));
                        }
                        catch (ObjectDisposedException) { break; }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Instance] 唤醒监听启动失败: {ex.Message}");
            }
        }

        /// <summary>通知已在运行的实例显示主窗口；失败也不影响本次退出</summary>
        private void SignalExistingInstance()
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(InstanceSignalName);
                signal.Set();
                WriteDiagnosticLog("[Instance] 已有实例在运行，已请求其显示主窗口；本次启动退出");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Instance] 唤醒已有实例失败（可能对方版本较旧）: {ex.GetType().Name}");
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 卸载清理：setup.iss 的 UninstallRun 会调用 --uninstall。
            // 注意：必须放在单实例判定之后执行，否则运行中的实例会把配置/日志重新写回，导致清理不干净。
            var isUninstall = e.Args.Contains("--uninstall");

            // 检测是否已有实例在运行（互斥锁）
            bool isFirstInstance;
            Mutex instanceMutex = null;
            try
            {
                instanceMutex = new Mutex(true, InstanceMutexName, out isFirstInstance);
            }
            catch { isFirstInstance = true; }

            if (!isFirstInstance && isUninstall)
            {
                // 卸载流程：静默结束所有实例（含提权实例），不弹任何对话框
                try
                {
                    foreach (var proc in Process.GetProcessesByName("SeewoAutoLogin")
                        .Where(p => p.Id != Environment.ProcessId))
                    {
                        try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                    }
                }
                catch (Exception ex) { WriteDiagnosticLog($"[Uninstall] 结束旧实例失败: {ex.Message}"); }

                instanceMutex?.Dispose();
                instanceMutex = new Mutex(true, InstanceMutexName, out isFirstInstance);
            }
            else if (!isFirstInstance)
            {
                // 已有实例在运行：请它把主窗口显示出来，本次启动随后静默退出。
                //
                // 原先这里弹一个带系统提示音的「是/否/取消」询问框。开机自启与本人在桌面上
                // 双击图标很容易撞在一起，于是每次开机都可能弹一个突兀的对话框加提示音 ——
                // 而用户真正想要的往往只是「把界面叫出来」。现在改为直接唤醒已有实例。
                SignalExistingInstance();
                instanceMutex?.Dispose();
                _isExiting = true;
                Shutdown();
                return;
            }

            if (isUninstall)
            {
                try { CleanupForUninstall(); }
                catch (Exception ex) { WriteDiagnosticLog($"[Uninstall] 清理失败: {ex.Message}"); }
                _isExiting = true;
                Shutdown();
                return;
            }

            // 保存互斥锁引用，确保在进程退出前不释放
            if (isFirstInstance && instanceMutex != null)
            {
                _instanceMutex = instanceMutex;
                StartInstanceSignalListener();
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

            // WebView2 预热：提前在后台创建运行时环境与浏览器进程，缩短主界面首次加载的等待
            // （托盘常驻场景同样预热，用户点开主界面时无需再等浏览器进程启动）
            Services.WebView2Runtime.Prewarm(WriteDiagnosticLog);

            // 开机自启自愈：配置要求自启、但系统里没有启动项（被杀软清理、程序换目录、旧版本从未写入）时自动重建
            try
            {
                if (_config.AutoStartEnabled)
                {
                    var autoStartModeNow = Services.AutoStartService.CurrentMode();
                    // 缺失或指向旧路径（换目录/被杀软清理）→ 重建
                    var needsRebuild = !Services.AutoStartService.IsEnabledForCurrentPath();
                    // 旧版本与安装包写入的是 HKCU 启动项（每次开机都要授权一次）；
                    // 管理员运行时顺带升级为「最高权限计划任务」，实现开机免 UAC
                    var needsUpgrade = autoStartModeNow == "registry" && Services.AutoStartService.IsAdministrator();

                    if (needsRebuild || needsUpgrade)
                    {
                        if (Services.AutoStartService.Enable(out var autoStartError, out var autoStartMode))
                            WriteDiagnosticLog(needsUpgrade
                                ? $"[AutoStart] 已将注册表启动项升级为计划任务; mode={autoStartMode}"
                                : $"[AutoStart] 自启缺失或指向旧路径，已按配置重建; mode={autoStartMode}");
                        else
                            WriteDiagnosticLog($"[AutoStart] 自启重建失败: {autoStartError}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[AutoStart] 自启自检异常: {ex.Message}");
            }

            // 启动 SSO 网关
            try
            {
                _gateway.Start();
                WriteDiagnosticLog("SSO 网关启动成功");
            }
            catch (Exception ex)
            {
                var hint = IsAdministrator()
                    ? "常见原因：端口 24300 被其它程序占用、hosts 文件无法写入。\n可在主界面顶部状态栏点击「一键修复」重试。"
                    : "当前以普通权限运行，本地 SSO 网关需要管理员权限。\n请以管理员身份重新运行本程序。";
                // 以普通权限运行导致网关起不来，是可预期的降级状态（用户主动选了"以降级模式运行"），
                // 不该用弹窗打断 —— 主界面自检栏会标红，托盘也会给出提示，足够让用户知道该提权。
                if (IsAdministrator())
                {
                    NotifyError("SSO 网关错误",
                        $"{ex.Message}\n\n" +
                        $"希沃自动登录功能可能无法正常工作。\n{hint}");
                }
                else
                {
                    WriteDiagnosticLog("[Gateway] 以普通权限运行，本地 SSO 网关未启动（预期内的降级状态，不弹窗）");
                    try { _trayIcon?.SetStatusText("需要管理员权限才能启用快捷登录"); } catch { }
                }
            }

            // 账号保活与网关是否可用无关：即便以普通权限运行、网关没能启动，
            // 也必须在凭据过期前续期，否则账号数据一样会失效。
            StartDailyTokenRefresh();

            // 关机 / 注销时也补一次续期（只挂一次，避免重复注册）
            try
            {
                Microsoft.Win32.SystemEvents.SessionEnding += (_, __) => TryFinalKeepAlive();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[KeepAlive] 关机事件注册失败: {ex.Message}");
            }

            // 首次启动显示欢迎界面（放在网关启动之后：即使用户停留在欢迎界面，希沃侧快捷登录也已可用）
            var welcomeShown = false;
            if (_config.IsFirstLaunch)
            {
                welcomeShown = true;
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

            // 显示主窗口（除非设置了启动隐藏或传了 --minimized）。
            // 例外：刚走完首次引导时一定显示 —— 否则用户答完教程询问后什么都看不到，会以为程序没启动。
            bool startMinimized = !welcomeShown && (e.Args.Contains("--minimized") || _config.StartMinimized);
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
            try
            {
                if (_overlay != null && _overlay.IsVisible)
                {
                    var closing = _overlay;
                    _overlay = null;
                    closing.Close();
                    return;
                }

                // 账号不足时遮罩会在构造阶段自行 Close，这里提前判断，避免“Show 已关闭窗口”抛异常
                var unlistedCount = Math.Max(0, _config.Accounts.Count - PluginConfig.MaxVisibleAccounts);
                if (unlistedCount == 0)
                {
                    NotifyInfo(Strings.AppTitle, $"当前 {_config.Accounts.Count} 个账号都已在希沃生效区（上限 {PluginConfig.MaxVisibleAccounts} 个），无需切换遮罩。");
                    return;
                }

                var overlay = new SeewoOverlay();
                overlay.Closed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) _overlay = null; };
                _overlay = overlay;
                overlay.Show();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Overlay] 显示切换遮罩失败: {ex.GetType().Name} - {ex.Message}");
                NotifyError("显示遮罩失败", "无法显示账号切换遮罩：\n" + ex.Message);
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

        /// <summary>托盘切换账号属于配置变更：启用密码保护时要求先验证密码（返回 true 表示应拒绝执行）</summary>
        private bool RequiresPasswordUnlock()
        {
            if (!_config.UsePluginPassword || string.IsNullOrEmpty(_config.PluginPasswordHash))
                return false;

            if (Services.PasswordService.IsLockedOut(out var secondsRemaining))
            {
                NotifyError(Strings.AppTitle, $"密码错误次数过多，请在 {secondsRemaining} 秒后重试。");
                return true;
            }

            try
            {
                // 注意：isPassword 必须通过构造函数传入，用对象初始化器赋值不会切换输入框可见性（会导致明文回显 + 校验恒失败）
                var owner = _mainWindow != null && _mainWindow.IsLoaded ? _mainWindow : null;
                var dlg = new TextInputDialog(Strings.AppTitle, Strings.EnterPassword, "", isPassword: true)
                {
                    Owner = owner
                };
                if (dlg.ShowDialog() != true) return true; // 取消 = 不执行
                return !VerifyPluginPassword(dlg.InputText);
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Tray] 密码验证对话框异常: {ex.GetType().Name}");
                return true; // fail-closed：校验过程出错时按“需要解锁”处理，不允许静默绕过
            }
        }

        /// <summary>校验应用设置口令（PBKDF2 + DPAPI；兼容旧的单轮 SHA-256 并在成功时自动升级）</summary>
        internal bool VerifyPluginPassword(string password)
        {
            var ok = Services.PasswordService.Verify(
                password, _config.PluginPasswordHash, _config.PluginPasswordSalt, out var upgraded);
            if (ok && upgraded)
            {
                try
                {
                    Services.PasswordService.Create(password, out var hash, out var salt);
                    _config.PluginPasswordHash = hash;
                    _config.PluginPasswordSalt = salt;
                    SaveConfig();
                    WriteDiagnosticLog("[Security] 设置口令哈希已升级为 PBKDF2 + 加盐 + DPAPI 保护");
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Security] 口令哈希升级失败: {ex.Message}");
                }
            }
            return ok;
        }

        /// <summary>设置/清除应用设置口令（hash 由 PasswordService 生成，落盘前已用 DPAPI 包裹）</summary>
        internal void SetPluginPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                _config.UsePluginPassword = false;
                _config.PluginPasswordHash = "";
                _config.PluginPasswordSalt = "";
            }
            else
            {
                Services.PasswordService.Create(password, out var hash, out var salt);
                _config.UsePluginPassword = true;
                _config.PluginPasswordHash = hash;
                _config.PluginPasswordSalt = salt;
            }
            SaveConfig();
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

        #region Self Check / Health / Batch Import

        /// <summary>自检状态（供主界面状态栏展示）</summary>
        internal object BuildSelfCheckStatus() => new
        {
            gatewayRunning = _gateway?.IsRunning == true,
            gatewayPort = _gateway?.Port ?? 0,
            expectedPort = SeewoSsoGateway.SeewoExpectedPort,
            gatewayPortOk = _gateway != null && !_gateway.IsPortMismatched,
            gatewayPortWarning = _gateway != null && _gateway.IsPortMismatched
                ? $"网关端口 {_gateway.Port} ≠ 希沃固定请求的 {SeewoSsoGateway.SeewoExpectedPort}，希沃不会显示快捷登录入口"
                : "",
            hostsOk = Services.HostsFileService.HasLoopbackMapping(),
            hostsState = Services.HostsFileService.DescribeState(),
            isAdmin = IsAdministrator(),
            seewoRunning = Process.GetProcessesByName("EasiNote").Length > 0,
            autoStartEnabled = Services.AutoStartService.IsEnabled,
            autoStartMode = Services.AutoStartService.CurrentMode(),
            autoStartText = Services.AutoStartService.DescribeState(),
            lastBackup = Services.ConfigBackupService.DescribeLatest(),
            maxVisibleAccounts = PluginConfig.MaxVisibleAccounts
        };

        /// <summary>一键修复：重写 hosts 映射并重启 SSO 网关</summary>
        internal async Task<string> RepairSsoAsync()
        {
            var messages = new List<string>();

            if (Services.HostsFileService.EnsureLoopbackMapping(out var hostsError))
                messages.Add("hosts 映射已修复");
            else
                messages.Add("hosts 修复失败：" + hostsError);

            try
            {
                if (_gateway.IsRunning) _gateway.Stop();
                await Task.Run(() => _gateway.Start()).ConfigureAwait(true);
                messages.Add(_gateway.IsPortMismatched
                    ? $"SSO 网关已启动，但端口为 {_gateway.Port}（希沃固定请求 {SeewoSsoGateway.SeewoExpectedPort}），快捷登录仍不会出现"
                    : $"SSO 网关已启动（端口 {_gateway.Port}）");
            }
            catch (Exception ex)
            {
                messages.Add("网关启动失败：" + ex.Message);
            }

            if (!IsAdministrator())
                messages.Add("当前不是管理员权限，hosts 与网关可能无法生效");

            var summary = string.Join("；", messages);
            WriteDiagnosticLog("[SelfCheck] 一键修复: " + summary);
            return summary;
        }

        /// <summary>
        /// 账号健康巡检：逐个校验凭据是否仍然有效，并就地修复。
        ///
        /// 与后台保活共用同一套逻辑：密码账号会用保存的密码重新登录，
        /// 扫码账号会换发新令牌（换发成功即写回本地凭据）。
        /// 确实修不了的（例如扫码令牌已过期）会标为异常，并说明需要人工做什么。
        /// </summary>
        internal async Task RunHealthCheckAsync()
        {
            WriteDiagnosticLog("[Health] 开始账号健康巡检（发现问题会自动重新导入）");
            var (refreshed, failed) = await RunKeepAliveAsync(force: true).ConfigureAwait(true);
            WriteDiagnosticLog($"[Health] 巡检完成：已自动修复 {refreshed} 个，仍需人工处理 {failed} 个");
        }

        /// <summary>批量导入账号：每行 “账号,密码[,备注]”</summary>
        internal (int added, int failed, List<string> messages) BatchImport(string text)
        {
            var added = 0;
            var failed = 0;
            var messages = new List<string>();

            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var parts = line.Split(new[] { ',', '，', '\t', ';' }).Select(p => p.Trim()).ToArray();
                if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    failed++;
                    messages.Add($"格式错误（需要 账号,密码[,备注]）：{line}");
                    continue;
                }

                var username = parts[0];
                var password = parts[1];
                var note = parts.Length >= 3 ? parts[2] : "";

                if (_config.Accounts.Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    failed++;
                    messages.Add($"已存在，跳过：{username}");
                    continue;
                }

                try
                {
                    var account = new SeewoAccount
                    {
                        Username = username,
                        Password = Services.SecureStore.Encrypt(password),
                        DisplayName = string.IsNullOrWhiteSpace(note) ? username : note
                    };
                    _config.Accounts.Add(account);
                    if (_config.Accounts.Count == 1) _config.ActiveAccountId = account.Id;
                    added++;
                    messages.Add($"已导入：{username}");
                }
                catch (Exception ex)
                {
                    failed++;
                    messages.Add($"导入失败 {username}：{ex.Message}");
                }
            }

            if (added > 0) SaveConfig();
            WriteDiagnosticLog($"[BatchImport] 成功 {added} 个，失败 {failed} 个");
            return (added, failed, messages);
        }

        #endregion

        /// <summary>
        /// 重启应用（以管理员身份）
        /// </summary>
        internal void RestartApp()
        {
            var started = false;
            try
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--elevated",
                        UseShellExecute = true
                    };
                    // 已经是管理员时不再请求提权，否则每次重启都会再弹一次 UAC
                    if (!IsAdministrator()) psi.Verb = "runas";
                    Process.Start(psi);
                    started = true;
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Restart] 重启失败（用户取消或系统限制）: {ex.Message}");
            }

            if (!started)
            {
                // 启动不成功时保持当前实例继续运行，避免托盘常驻程序“凭空消失”
                NotifyError("重启失败", "无法自动重启，请从托盘菜单退出后重新打开程序。");
                return;
            }

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

        /// <summary>后台保活的检查频率：每 5 分钟看一次有哪些账号该续期了</summary>
        private static readonly TimeSpan KeepAliveTick = TimeSpan.FromMinutes(5);
        /// <summary>账号超过这么久没有续期，就主动刷新一次</summary>
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(25);

        private bool _keepAliveRunning;

        /// <summary>
        /// 是否为占位账号（由「添加假账号」生成，凭据在希沃侧并不存在）。
        /// 判断依据是账号上的显式标记，不做命名猜测 —— 用户完全可以把某个账号命名为 test 之类的名字。
        /// </summary>
        private static bool IsPlaceholderAccount(SeewoAccount account) => account?.IsPlaceholder == true;

        /// <summary>
        /// 兼容历史数据：早期版本生成的假账号没有标记，这里按当时固定的命名补上（仅在必要时执行一次）。
        /// 补完之后判断就完全依赖标记。
        /// </summary>
        private void MigratePlaceholderFlags()
        {
            var changed = false;
            foreach (var account in _config.Accounts)
            {
                if (account.IsPlaceholder) continue;
                if ((account.Id ?? "").StartsWith("FAKE_", StringComparison.OrdinalIgnoreCase)
                    || (account.Username ?? "").StartsWith("fake_fake_", StringComparison.OrdinalIgnoreCase))
                {
                    account.IsPlaceholder = true;
                    changed = true;
                    WriteDiagnosticLog($"[Config] 历史测试账号已补占位标记; account-id={account.Id}");
                }
            }

            if (changed) SaveConfig();
        }

        /// <summary>
        /// 启动账号凭据的后台保活。
        ///
        /// 希沃的登录令牌会随时间失效，一旦失效，该账号在希沃的登录界面上就无法再用于快捷登录，
        /// 表现为“账号数据已过期”。而换发令牌本身需要“用当前令牌换新令牌”，
        /// 所以必须在令牌失效之前主动续期 —— 事后补救是来不及的。
        ///
        /// 原先的实现是 `Timer(..., FromDays(1), FromDays(1))`：启动满一天才执行第一次，
        /// 而程序每次重启计时器都从头开始，实际几乎从不触发；且它只处理扫码账号，
        /// 密码账号完全不刷新。这里改为每 5 分钟检查、超过 25 分钟未续期即刷新，两类账号都覆盖。
        /// </summary>
        private void StartDailyTokenRefresh()
        {
            if (_dailyTokenRefreshTimer != null) return;

            // 启动后 20 秒先跑一轮（等界面与网关就绪），此后每 5 分钟检查一次
            _dailyTokenRefreshTimer = new Timer(_ => _ = RunKeepAliveAsync(), null,
                TimeSpan.FromSeconds(20), KeepAliveTick);

            WriteDiagnosticLog($"[KeepAlive] 后台保活已启动：每 {KeepAliveTick.TotalMinutes:0} 分钟检查，" +
                               $"账号超过 {KeepAliveInterval.TotalMinutes:0} 分钟未续期即自动刷新");
        }

        /// <summary>
        /// 后台保活：在凭据失效前主动续期，返回（成功数, 失败数）。
        /// force=true 时忽略时间间隔 —— 供「健康巡检」按钮手动触发，即“发现问题就地修好”。
        /// </summary>
        internal async Task<(int refreshed, int failed)> RunKeepAliveAsync(bool force = false)
        {
            if (_keepAliveRunning) return (0, 0);
            _keepAliveRunning = true;

            var refreshed = 0;
            var failed = 0;
            var stateChanged = false;
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var account in _config.Accounts.ToList())
                {
                    // 测试 / 占位账号不参与保活，也不显示健康状态
                    if (IsPlaceholderAccount(account))
                    {
                        if (!string.IsNullOrEmpty(account.HealthState) || !string.IsNullOrEmpty(account.HealthMessage))
                        {
                            account.HealthState = "";
                            account.HealthMessage = "";
                            stateChanged = true;
                        }
                        continue;
                    }

                    if (!force && account.LastTokenExchangeAtUtc.HasValue &&
                        now - account.LastTokenExchangeAtUtc.Value < KeepAliveInterval)
                        continue;

                    var (ok, message) = await RefreshAccountCredentialAsync(account).ConfigureAwait(true);
                    account.HealthState = ok ? "ok" : "bad";
                    account.HealthMessage = message;
                    account.LastHealthCheckAtUtc = DateTime.UtcNow;

                    stateChanged = true;
                    if (ok)
                    {
                        account.LastTokenExchangeAtUtc = DateTimeOffset.UtcNow;
                        refreshed++;
                        if (force) WriteDiagnosticLog($"[KeepAlive] 续期成功; account-id={account.Id}; {message}");
                    }
                    else
                    {
                        failed++;
                        WriteDiagnosticLog($"[KeepAlive] 续期失败; account-id={account.Id}; reason={message}");
                    }
                }

                if (stateChanged)
                {
                    SaveConfig();
                    await RefreshAccountListUiAsync().ConfigureAwait(true);
                }

                if (refreshed > 0 || failed > 0)
                    WriteDiagnosticLog($"[KeepAlive] 本轮完成：续期 {refreshed} 个，失败 {failed} 个");

                if (failed > 0) NotifyKeepAliveFailure();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[KeepAlive] 本轮异常: {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                _keepAliveRunning = false;
            }

            return (refreshed, failed);
        }

        /// <summary>
        /// 刷新单个账号的凭据。
        /// 使用独立的服务实例，避免与正在响应希沃请求的网关争用同一份会话状态。
        /// </summary>
        private async Task<(bool ok, string message)> RefreshAccountCredentialAsync(SeewoAccount account)
        {
            try
            {
                // 密码账号：用保存的密码重新登录，顺带刷新用户信息
                if (!string.IsNullOrEmpty(account.Password))
                {
                    var password = account.DecryptedPassword;
                    if (string.IsNullOrEmpty(password))
                        return (false, "本地凭据无法解密，请重新录入密码");

                    using var service = new SeewoAuthService();
                    service.DiagnosticMessage += WriteDiagnosticLog;
                    var result = await service.LoginAsync(account.Username, password).ConfigureAwait(true);
                    if (!result.Success) return (false, result.ErrorMessage ?? "密码登录失败");
                    if (result.UserInfo != null) account.UserInfo = result.UserInfo;
                    return (true, "已自动重新登录");
                }

                // 扫码账号：用当前令牌换发新令牌，换发成功后立即写回本地加密凭据
                if (!string.IsNullOrWhiteSpace(account.QrCredentialId))
                {
                    if (!_qrSessionStore.TryLoad(account.QrCredentialId, out var session))
                        return (false, "扫码凭据不可用，需要重新扫码");

                    using var service = new SeewoAuthService();
                    service.DiagnosticMessage += WriteDiagnosticLog;
                    service.RestoreQrSession(session.Token, account.UserInfo);
                    var result = await service.ExchangeCurrentTokenAsync().ConfigureAwait(true);

                    // 记录凭据「年龄」：凭据是扫码那一刻拿到的，能续期说明还在有效期内。
                    // 积累几次「多大年龄仍能续期 / 从多大年龄开始续不动」，就能反推出实际有效期，
                    // 从而判断关机多久之内还能自动恢复。
                    var age = session.AcquiredAtUtc == default
                        ? "未知"
                        : (DateTimeOffset.UtcNow - session.AcquiredAtUtc).TotalHours.ToString("F1") + " 小时";

                    if (!result.Success)
                    {
                        WriteDiagnosticLog($"[KeepAlive] 扫码凭据续期失败; account-id={account.Id}; 凭据年龄={age}; " +
                                           $"说明=凭据已超出有效期，无法自动恢复，需要重新扫码");
                        return (false, "扫码令牌已失效，需要重新扫码添加");
                    }

                    WriteDiagnosticLog($"[KeepAlive] 扫码凭据续期成功; account-id={account.Id}; 凭据年龄={age}");
                    if (!string.IsNullOrWhiteSpace(service.Token)) OnQrTokenValidated(account, service.Token);
                    if (service.UserInfo != null) account.UserInfo = service.UserInfo;
                    return (true, "已自动续期");
                }

                return (false, "没有可用于续期的凭据");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>上一次就续期失败弹窗的日期（弹窗每天最多一次）</summary>
        private DateTime _lastKeepAliveNotifyDate = DateTime.MinValue;

        /// <summary>
        /// 续期失败时的提醒策略。
        ///
        /// 续期是后台行为，失败原因往往只是网络抖动；每次都弹窗（还带系统提示音）
        /// 会在上课时突然响一声，很打扰。所以这里分三档：
        ///   · 托盘图标右下角点亮感叹号 —— 只要还有异常账号就一直亮着，恢复后自动熄灭
        ///   · 账号列表里逐条标注状态 —— 想细看的时候随时能看
        ///   · 弹窗每天最多一次，并列出具体是哪些账号
        /// </summary>
        private void NotifyKeepAliveFailure()
        {
            try
            {
                var bad = _config.Accounts
                    .Where(a => a.HealthState == "bad" && !IsPlaceholderAccount(a))
                    .ToList();

                // 托盘感叹号跟随实际状态，恢复正常后自动熄灭
                _trayIcon?.SetAlert(bad.Count > 0);

                if (bad.Count == 0) return;

                var today = DateTime.Today;
                if (_lastKeepAliveNotifyDate == today) return;
                _lastKeepAliveNotifyDate = today;

                var shown = bad.Take(8)
                    .Select(a => string.IsNullOrWhiteSpace(a.DisplayName) ? a.Username : a.DisplayName)
                    .ToList();
                var nameList = string.Join("\n", shown.Select(n => "  · " + n));
                if (bad.Count > shown.Count) nameList += $"\n  · 另有 {bad.Count - shown.Count} 个";

                var qrCount = bad.Count(a => !string.IsNullOrWhiteSpace(a.QrCredentialId));
                var pwdCount = bad.Count - qrCount;
                var hint = new List<string>();
                if (pwdCount > 0) hint.Add($"{pwdCount} 个密码账号：请确认密码是否已修改");
                if (qrCount > 0) hint.Add($"{qrCount} 个扫码账号：需要重新扫码添加");

                NotifyInfo("有账号自动续期失败",
                    $"以下账号需要处理：\n\n{nameList}\n\n{string.Join("\n", hint)}" +
                    "\n\n后续不再重复弹窗，可查看托盘图标或账号列表了解状态。");
            }
            catch { }
        }

        /// <summary>
        /// 退出前补一次续期。
        ///
        /// 凭据越「新鲜」，关机后还能撑的时间越长：如果凭据有效期是若干天，
        /// 那么在关机前刚换过一次，下次开机这段时间内都还有机会继续续期。
        /// 只在确实有账号需要续期时才做，且带超时，不会拖慢退出。
        /// </summary>
        private void TryFinalKeepAlive()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var need = _config.Accounts.Any(a =>
                    !IsPlaceholderAccount(a) &&
                    (!string.IsNullOrEmpty(a.Password) || !string.IsNullOrWhiteSpace(a.QrCredentialId)) &&
                    (!a.LastTokenExchangeAtUtc.HasValue || now - a.LastTokenExchangeAtUtc.Value > TimeSpan.FromMinutes(5)));

                if (!need) return;

                WriteDiagnosticLog("[KeepAlive] 退出前刷新凭据…");
                var task = RunKeepAliveAsync(force: true);
                if (!task.Wait(TimeSpan.FromSeconds(12)))
                    WriteDiagnosticLog("[KeepAlive] 退出前刷新超时，已跳过（不影响退出）");
                else
                    WriteDiagnosticLog($"[KeepAlive] 退出前刷新完成：续期 {task.Result.refreshed} 个");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[KeepAlive] 退出前刷新异常: {ex.GetType().Name}");
            }
        }

        /// <summary>让主界面刷新账号列表（主界面未打开或已销毁时静默跳过）</summary>
        private async Task RefreshAccountListUiAsync()
        {
            try
            {
                if (MainWindow is ManagementWindow window && window.IsLoaded)
                    await window.RefreshAccountListAsync().ConfigureAwait(true);
            }
            catch { }
        }

        #endregion

        #region Config Persistence

        /// <summary>配置与日志的并发保护：UI 线程与网关 HTTP 线程都会读写配置/写日志</summary>
        private static readonly object ConfigIoLock = new object();
        private static readonly object LogIoLock = new object();
        private const int LogRotationBytes = 5 * 1024 * 1024;
        private const int LogRetentionDays = 14;
        private static DateTime _lastLogCleanupDate = DateTime.MinValue;

        private static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeewoAutoLogin");

        private string ConfigPath => Path.Combine(AppDataDir, "config.json");

        public void LoadConfig()
        {
            lock (ConfigIoLock)
            {
                var loaded = TryLoadConfigFile(ConfigPath)
                             ?? TryLoadConfigFile(ConfigPath + ".bak")
                             ?? TryLoadLatestBackup();

                if (loaded == null)
                {
                    _config = new PluginConfig();
                    return;
                }

                loaded.Accounts ??= new List<SeewoAccount>();
                loaded.Accounts.RemoveAll(a => a == null);
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

        private PluginConfig TryLoadConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded == null) WriteDiagnosticLog($"[Config] 配置解析结果为空: {path}");
                return loaded;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Config] 配置读取失败({Path.GetFileName(path)}): {ex.Message}");
                return null;
            }
        }

        private PluginConfig TryLoadLatestBackup()
        {
            try
            {
                var latest = Services.ConfigBackupService.List().FirstOrDefault();
                if (latest == null) return null;
                if (!Services.ConfigBackupService.TryRead(latest.Name, out var json, out _)) return null;
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded != null)
                    WriteDiagnosticLog($"[Config] 主配置与 .bak 均不可用，已回退到备份 {latest.Name}");
                return loaded;
            }
            catch { return null; }
        }

        public void SaveConfig()
        {
            lock (ConfigIoLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(ConfigPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // 加密明文/旧格式密码；加密失败会抛异常，由外层 catch 中止本次保存，绝不把明文写盘
                    EnsurePasswordsEncrypted();

                    var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });

                    // 原子写：先写临时文件，再替换，避免进程中断留下半截 JSON（会导致账号全部丢失）
                    var temp = ConfigPath + ".tmp";
                    File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                    if (File.Exists(ConfigPath))
                    {
                        try { File.Replace(temp, ConfigPath, ConfigPath + ".bak", ignoreMetadataErrors: true); }
                        catch
                        {
                            File.Copy(temp, ConfigPath, overwrite: true);
                            try { File.Delete(temp); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(temp, ConfigPath);
                    }

                    Services.ConfigBackupService.Archive(json);
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"保存配置失败: {ex.Message}");
                }
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
            // 1) 结束开机自启（计划任务 + 注册表启动项），否则会残留指向已删除 exe 的启动项
            try
            {
                if (Services.AutoStartService.Disable(out var autoStartError))
                    WriteDiagnosticLog("[Uninstall] 已移除开机自启");
                else
                    WriteDiagnosticLog($"[Uninstall] 移除开机自启失败: {autoStartError}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 移除开机自启异常: {ex.Message}");
            }

            // 2) hosts：只删除本程序写入的行（带标记或精确匹配的旧行），按原编码原子写回
            try
            {
                if (Services.HostsFileService.RemoveLoopbackMapping(out var hostsError))
                    WriteDiagnosticLog($"[Uninstall] 已移除 hosts 中的 {Services.HostsFileService.HostName} 映射（原始备份：{Services.HostsFileService.BackupPath}）");
                else
                    WriteDiagnosticLog($"[Uninstall] 清理 hosts 失败（可能需要管理员权限）: {hostsError}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 清理 hosts 异常: {ex.Message}");
            }

            // 3) 删除数据目录
            try
            {
                if (Directory.Exists(AppDataDir))
                {
                    Directory.Delete(AppDataDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 删除数据目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载窗口图标（嵌入资源里的 app.ico）。
        /// 窗口若不显式设置 Icon，只会沿用 exe 图标并受 Windows 图标缓存影响，
        /// 换图标后任务栏仍显示旧图标，所以这里直接给出图像。
        /// </summary>
        internal static System.Windows.Media.Imaging.BitmapSource? LoadWindowIcon()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) return null;

                // 关键：ico 里打包了 16~256 多个尺寸，直接交给 BitmapImage 会取到第一帧（通常是 16px），
                // 任务栏和 Alt+Tab 再把它放大，于是图标又小又糊。这里按尺寸挑一帧合适的。
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                var frame = decoder.Frames
                    .Where(f => f.PixelWidth <= 64)          // 32/48 足够任务栏使用
                    .OrderByDescending(f => f.PixelWidth)
                    .FirstOrDefault()
                    ?? decoder.Frames.OrderBy(f => f.PixelWidth).First();   // 兜底取最小帧

                frame.Freeze();
                return frame;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读取用户协议正文（嵌入资源）。欢迎界面的协议页与「关于」页的弹窗共用这一份，
        /// 保证两处内容永远一致。
        /// </summary>
        internal static string LoadTermsText()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("Resources.terms.txt", StringComparison.Ordinal));
                if (name == null) return "";

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) return "";
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 恢复出厂设置：清空账号、扫码凭据、配置与备份，回到首次安装状态。
        /// 日志保留（便于排障），下次启动会重新走欢迎界面。
        /// </summary>
        internal void FactoryReset()
        {
            try { _authService?.Logout(); } catch { }

            foreach (var folder in new[] { "Sessions", "Backups" })
            {
                try
                {
                    var path = Path.Combine(AppDataDir, folder);
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Reset] 清理 {folder} 失败: {ex.Message}");
                }
            }

            lock (ConfigIoLock)
            {
                _config = new PluginConfig();
            }
            SaveConfig();
            WriteDiagnosticLog("[Reset] 已恢复出厂设置（账号、设置与备份均已清空）");
        }

        /// <summary>列出配置备份（供设置页「配置备份」区域展示）</summary>
        internal List<Services.ConfigBackupService.BackupItem> ListConfigBackups()
            => Services.ConfigBackupService.List();

        /// <summary>从指定备份恢复配置</summary>
        internal bool RestoreConfigBackup(string name, out string error)
        {
            error = null;
            if (!Services.ConfigBackupService.TryRead(name, out var json, out error)) return false;
            try
            {
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded == null)
                {
                    error = "备份内容无法解析";
                    return false;
                }
                loaded.Accounts ??= new List<SeewoAccount>();
                loaded.Accounts.RemoveAll(a => a == null);
                lock (ConfigIoLock) { _config = loaded; }
                SaveConfig();
                WriteDiagnosticLog($"[Config] 已从备份 {name} 恢复配置（{_config.Accounts.Count} 个账号）");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                WriteDiagnosticLog($"[Config] 恢复备份失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出配置到文件
        /// </summary>
        internal void ExportConfig(string filePath)
        {
            try
            {
                // 导出前同样做加密迁移：避免历史遗留的明文密码被写进导出文件
                lock (ConfigIoLock)
                {
                    EnsurePasswordsEncrypted();
                    var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, json);
                }
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
                // 校验导入内容：剔除空账号与非法 Id（Id 会进入前端 DOM 与 SSO 请求路径）
                loaded.Accounts.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 64);
                foreach (var account in loaded.Accounts)
                {
                    account.HealthState = "";
                    account.HealthMessage = "";
                }
                lock (ConfigIoLock) { _config = loaded; }
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
            System.Diagnostics.Debug.WriteLine($"[SeewoAutoLogin] {message}");
            try
            {
                var baseDir = Path.Combine(AppDataDir, "Logs");
                var path = Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}";
                lock (LogIoLock)
                {
                    Directory.CreateDirectory(baseDir);
                    RotateLogIfNeeded(path);
                    // FileShare.ReadWrite：网关/定时器/UI 多线程并发写日志时不再互相抛 IOException（丢日志）
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                    CleanupOldLogs(baseDir);
                }
            }
            catch { }
        }

        /// <summary>单个日志文件超过上限后滚动为 .1.log，避免长期驻留无限增长</summary>
        private static void RotateLogIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < LogRotationBytes) return;
                var rolled = path + ".1";
                if (File.Exists(rolled)) File.Delete(rolled);
                File.Move(path, rolled);
            }
            catch { }
        }

        /// <summary>每天最多清理一次：删除超过保留期的日志</summary>
        private static void CleanupOldLogs(string baseDir)
        {
            try
            {
                if (_lastLogCleanupDate == DateTime.Today) return;
                _lastLogCleanupDate = DateTime.Today;
                var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
                foreach (var file in new DirectoryInfo(baseDir).GetFiles("*.log*"))
                {
                    if (file.LastWriteTime < cutoff)
                    {
                        try { file.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        #endregion
    }
}
