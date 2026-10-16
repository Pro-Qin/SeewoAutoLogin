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
        /// <summary>凭据有效期观测（只写本机）</summary>
        private Services.CredentialLifetimeTracker _lifetimeTracker;
        /// <summary>切换账号口令服务</summary>
        internal Services.SwitchPinService SwitchPin => _switchPin;

        /// <summary>凭据有效期的观测结论（供设置页展示；样本不足时返回提示语，不编造）</summary>
        internal string CredentialLifetimeDescription
        {
            get
            {
                try { return _lifetimeTracker?.DescribeBounds() ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>本机功能使用统计（只写本机，不上报）</summary>
        private Services.LocalUsageStats _usageStats;
        /// <summary>切换账号口令</summary>
        private Services.SwitchPinService _switchPin;
        private readonly SeewoUserListRotationService _userListRotation;
        private readonly SeewoSsoGateway _gateway;
        private readonly TrayIconService _trayIcon;
        private UpdateCoordinator _updateCoordinator;

        /// <summary>当前程序版本（三段式）。</summary>
        private static string CurrentAppVersion
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        /// <summary>后台静默更新是否正在运行（供主界面手动更新入口判断，避免重复下载）。</summary>
        /// <summary>后台静默更新的当前状态（没有后台任务时返回一份 idle 快照）。</summary>
        internal Services.SilentUpdateSnapshot SilentUpdateSnapshot()
            => _updateCoordinator?.Snapshot() ?? new Services.SilentUpdateSnapshot();

        /// <summary>掐断后台正在进行的下载，让前台任务接手。返回是否真的掐断过。</summary>
        internal Task<bool> AbortSilentUpdateAsync(string reason)
            => _updateCoordinator?.AbortDownloadAsync(reason) ?? Task.FromResult(false);

        /// <summary>已在后台下载好、等待安装的版本号（没有则为空串）。</summary>
        internal string PendingUpdateVersion
            => string.IsNullOrWhiteSpace(_config?.PendingUpdateStage) ? "" : (_config.PendingUpdateVersion ?? "");
        private PluginConfig _config = new PluginConfig();
        private Timer _dailyTokenRefreshTimer;
        private ManagementWindow _mainWindow;
        private SeewoOverlay _overlay;
        public SeewoOverlay CurrentOverlay => _overlay;
        private Mutex _instanceMutex;
        private bool _isExiting;
        /// <summary>本次启动是否为更新后的第一次启动（安装器传入 --updated）。</summary>
        private bool _isPostUpdateBoot;
        /// <summary>静默更新退出时跳过 hosts 恢复，避免安装期间希沃请求落空。</summary>
        private bool _skipHostsRestoreOnExit;
        /// <summary>
        /// 网关是否因为「非管理员」这一预期原因没能启动。
        /// 更新后的健康确认必须放行这种情况：否则普通权限运行的用户每次升级都会被回滚保护判定为「新版不健康」，
        /// 装好又被换回旧版，来回折腾。
        /// </summary>
        private bool _gatewayDegradedDueToPermissions;
        /// <summary>后台静默更新是否正在运行（0/1，Interlocked 保护）。</summary>
        /// <summary>希沃客户端版本变化检测结果（启动时检测一次，供主界面首次显示时提示）</summary>
        private Services.SeewoVersionCheckResult _seewoVersionChange;

        /// <summary>
        /// True when the application is performing an intentional shutdown (not minimize-to-tray).
        /// </summary>
        internal bool IsExiting => _isExiting;

        /// <summary>主界面开场动画是否已播放（每个进程只播放一次）</summary>
        internal bool IntroPlayed { get; set; }

        /// <summary>本次启动检测到的希沃客户端版本变化（无变化 / 未检测到时为 null 或 Changed=false）</summary>
        internal Services.SeewoVersionCheckResult SeewoVersionChange => _seewoVersionChange;

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
            _lifetimeTracker = new Services.CredentialLifetimeTracker(AppDataDir);
            _usageStats = new Services.LocalUsageStats(AppDataDir);
            _switchPin = new Services.SwitchPinService(AppDataDir);
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

            _updateCoordinator = new UpdateCoordinator(
                _config, WriteDiagnosticLog, SaveConfig,
                status => _trayIcon?.SetStatusText(status),
                BeginSilentUpdateExit,
                action => Dispatcher.Invoke(action),
                // 后台下载时把进度回显到主界面（界面没开就什么也不做）
                progress => _mainWindow?.OnSilentUpdateProgress(progress),
                // 主界面开着的时候不自动安装：安装要重启程序，界面会「自己消失」，观感很差
                () => _mainWindow != null && _mainWindow.IsVisible);

            // 兜底清理：当前版本已经不低于「曾经装失败的版本」时，那条记录就没意义了
            // （多半是用户自己手动装上了）。留着它会让下一轮自动更新被误跳过。
            if (!string.IsNullOrWhiteSpace(_config.FailedUpdateVersion) &&
                UpdateChecker.CompareVersions(
                    typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    _config.FailedUpdateVersion) >= 0)
            {
                WriteDiagnosticLog($"[Update] 当前版本已不低于曾失败的 v{_config.FailedUpdateVersion}，清除该记录");
                _config.FailedUpdateVersion = "";
                SaveConfig();
            }

            // 注：更新后第一次启动的收尾（版本核对、待更新状态清理、健康确认）不在这里做。
            // _isPostUpdateBoot 要到 OnStartup 才从命令行读出来，放在构造函数里等于永远不执行 ——
            // 那样 UpdateHealthGuard 的回滚标记就永远没人清，watcher 超时会把刚装好的版本换回旧的。
            // 见 HandlePostUpdateBoot()。

            _gateway = new SeewoSsoGateway(_authService, () => _config, TryRestoreQrSession,
                GetVisibleAccounts,
                account => { account.UserInfo = _authService.UserInfo; SaveConfig(); },
                OnQrTokenValidated, _userListRotation, SaveConfig);
            // 希沃固定请求 24300：始终以该端口为首选（配置里记录的值只用于诊断展示，不作为首选端口）
            _gateway.Port = SeewoSsoGateway.SeewoExpectedPort;
            _gateway.ConfirmStopEasiAgent = (pid, path) =>
            {
                // 更新后第一次启动：静默接管 24300。EasiAgent 被结束后希沃会重新拉起它并重新请求账号列表，
                // 快捷登录随之恢复；这里不能再弹窗，否则静默更新就断在最后一步了。
                if (_isPostUpdateBoot)
                {
                    WriteDiagnosticLog($"[AutoRepair] 自动结束 EasiAgent 以接管 SSO 端口; pid={pid}; 更新后启动");
                    return true;
                }

                // 自动修复开启时也不再无条件结束希沃的进程：老师正在用希沃（最近 15 分钟内有过
                // 快捷登录请求）时，结束 EasiAgent 会直接打断他手上的操作。这时宁可降级到备用端口，
                // 等希沃空闲下来，下一轮再接管。
                if (_config?.AutoRepairEnabled == true)
                {
                    if (IsSeewoIdleForTakeover())
                    {
                        WriteDiagnosticLog($"[AutoRepair] 希沃空闲，自动结束 EasiAgent 以接管 SSO 端口; pid={pid}");
                        return true;
                    }
                    WriteDiagnosticLog($"[AutoRepair] 希沃可能正在使用，本次不结束 EasiAgent(pid={pid})，改用备用端口");
                    return false;
                }
                return Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"本地 SSO 网关端口被希沃 EasiAgent 占用（pid={pid}）。\n\n" +
                    "结束它可以立刻让快捷登录生效，但可能中断正在进行的希沃操作。\n\n" +
                    "  · 是   → 结束 EasiAgent 并使用该端口\n" +
                    "  · 否   → 不结束任何进程，自动改用备用端口（希沃可能仍请求原端口）",
                    Strings.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            };
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

            // 回滚 watcher 专用入口：不启动正常逻辑，只监控健康标记并在超时后回滚。
            if (e.Args.Contains("--rollback-watch"))
            {
                Services.UpdateHealthGuard.RunRollbackWatch(e.Args, WriteDiagnosticLog);
                _isExiting = true;
                Shutdown();
                return;
            }

            // 崩溃可观测性与安全模式：先注册异常处理并记录本次启动尝试。
            Services.CrashReporter.Install(this, WriteDiagnosticLog);
            // 启动失败的计数放到后面（真正开始跑之前），见下面 MarkStartupAttempt 的注释。
            var safeMode = Services.CrashReporter.IsSafeModeRequested;
            if (safeMode) WriteDiagnosticLog("[Crash] 本次启动进入安全模式：跳过 WebView2、遮罩与自动更新");

            // 安装器静默升级完成后会带 --updated 启动：用于跳过提权询问、自动接管 SSO 端口并清理待更新状态。
            _isPostUpdateBoot = e.Args.Contains("--updated");

            // 排查 Win7 上的渲染残影（界面上偶尔飘过的深色小条）：--software-render 强制 WebView2 走
            // 软件渲染。正常启动看一次、带这个参数看一次，就能判断残影是不是 GPU 合成造成的。
            // 属于诊断开关，不写进设置界面。
            if (e.Args.Contains("--software-render"))
            {
                Services.WebView2Runtime.ForceSoftwareRendering = true;
                WriteDiagnosticLog("[WebView2] 命令行指定强制软件渲染（--software-render）");
            }

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
                        .Where(p => p.Id != Process.GetCurrentProcess().Id))
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

            // 到这里才算「本次启动真的接管了这台机器」。唤醒已有实例、卸载清理这些路径在上面就返回了，
            // 不该记进启动失败次数 —— 原先写在最前面，双击几次图标攒够 3 次，下一次正常启动就会被
            // 误判成连续失败而进入安全模式（跳过界面与自动更新）。
            Services.CrashReporter.MarkStartupAttempt(WriteDiagnosticLog);
            if (!safeMode && Services.CrashReporter.IsSafeModeRequested)
            {
                safeMode = true;
                WriteDiagnosticLog("[Crash] 本次启动进入安全模式：跳过 WebView2、遮罩与自动更新");
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

            // 全局异常处理器已由 CrashReporter 注册。

            // Win7/8.1 上 HTTPS 能否跑通，取决于系统 Schannel 有没有启用 TLS 1.2 客户端：
            // 程序保持 ServicePointManager 的 SystemDefault（微软建议不要硬编码协议版本），
            // 改为把系统层的 TLS 1.2 打开（与微软 KB3140245 的指引一致）。
            // 写注册表需要管理员权限，所以放在提权判定之后。
            Services.NetworkCompat.EnsureTls12ForLegacyWindows(WriteDiagnosticLog);

            // 初始化托盘图标
            try
            {
                _trayIcon.Initialize();
                if (safeMode) _trayIcon.SetStatusText("安全模式：已跳过界面与自动更新");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"托盘初始化失败: {ex.Message}");
            }

            // 更新后收尾放在托盘初始化之后：它要写托盘提示，也要在版本没到位时把话说清楚
            if (_isPostUpdateBoot) HandlePostUpdateBoot();

            // WebView2 预热：提前在后台创建运行时环境与浏览器进程，缩短主界面首次加载的等待
            // （托盘常驻场景同样预热，用户点开主界面时无需再等浏览器进程启动）
            if (!safeMode) Services.WebView2Runtime.Prewarm(WriteDiagnosticLog);

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
                // 这段代码在 startMinimized 判断之前，开机自启时正好落在上课时间：
                // 无论是不是管理员，都不该用模态框把老师拦在启动路径上。
                // 主界面自检栏会标红，托盘提示也会写出同一件事，打开界面就能点「一键修复」。
                if (IsAdministrator())
                {
                    WriteDiagnosticLog("[Gateway] SSO 网关启动失败（管理员权限）: " + ex.Message
                        + "；常见原因：端口 24300 被其它程序占用、hosts 文件无法写入");
                    try { _trayIcon?.SetStatusText("SSO 网关未启动，可在主界面点「一键修复」"); } catch { }
                }
                else
                {
                    WriteDiagnosticLog("[Gateway] 以普通权限运行，本地 SSO 网关未启动（预期内的降级状态，不弹窗）");
                    _gatewayDegradedDueToPermissions = true;
                    try { _trayIcon?.SetStatusText("需要管理员权限才能启用快捷登录"); } catch { }
                }
            }

            // 希沃客户端版本变化检测（网关启动之后执行一次）：
            // 希沃升级后接口可能变化，越早知道越容易把「登录异常」归因到版本变化上。
            try
            {
                _seewoVersionChange = Services.SeewoVersionMonitor.CheckAndRecord(
                    Path.Combine(AppDataDir, "seewo-version.json"), WriteDiagnosticLog);

                // 只在网关正常时才说这句话：悬停提示只显示一条，网关起不来是更要紧的事，别把它顶掉
                if (_seewoVersionChange != null && _seewoVersionChange.Changed && _gateway?.IsRunning == true)
                    _trayIcon?.SetStatusText($"检测到希沃客户端已更新（{_seewoVersionChange.Previous} → {_seewoVersionChange.Current}）");
                if (!string.IsNullOrWhiteSpace(_seewoVersionChange?.Current))
                    WriteDiagnosticLog($"[SeewoVersion] 兼容性: {Services.CompatibilityMatrix.Describe(_seewoVersionChange.Current)}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[SeewoVersion] 版本检测异常: {ex.GetType().Name} - {ex.Message}");
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
            bool startMinimized = safeMode || (!welcomeShown && (e.Args.Contains("--minimized") || _config.StartMinimized));
            if (startMinimized)
            {
                // 同样只写悬停提示：开机自启恰好落在上课时间，启动弹通知是最容易被投诉的一种打扰
                try { _trayIcon?.SetStatusText(Strings.StartMinimized); } catch { }
                // 主界面不会自动打开：桌面提醒是用户唯一能直接看到「已经升级」的入口
                if (!safeMode) _ = ShowDesktopUpgradeNoticeIfNeededAsync();
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

            // ClassIsland 同款静默更新：后台检查、自动下载、静默安装，不依赖主界面是否打开。
            if (!safeMode && _config.AutoCheckUpdate && _updateCoordinator != null)
                _ = _updateCoordinator.RunAsync();

            // 启动 60 秒后仍然存活：清除失败计数与安全模式标记。
            _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
                Services.CrashReporter.MarkStartupSuccess(WriteDiagnosticLog));

            // 自动修复：启动后定时自检 hosts、网关与开机自启，不依赖主界面。
            _ = RunAutoRepairLoopAsync();
        }

        /// <summary>
        /// 遮罩被关掉过（用户手动关或它自己到点关）。
        ///
        /// 自动显示逻辑据此不再反复弹：此前只判「希沃在运行且未登录」，用户关掉后 2 秒内
        /// 又被弹出来，变成一个关不掉的窗口。希沃退出或登录成功后复位。
        /// </summary>
        internal bool OverlayDismissedByUser { get; set; }

        /// <summary>
        /// 现在能不能动手结束希沃的 EasiAgent。
        ///
        /// 唯一判据是「最近有没有在用」：15 分钟内有 SSOLOGIN 请求就算在用。
        /// 宁可多用一会儿备用端口，也不在老师讲课讲一半时把他的希沃掐掉。
        /// </summary>
        private static bool IsSeewoIdleForTakeover()
        {
            try
            {
                var last = Services.SeewoSsoGateway.LastSsologinUtc;
                return last == DateTime.MinValue || DateTime.UtcNow - last >= TimeSpan.FromMinutes(15);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 更新后第一次启动的收尾：核对版本是否真的到位、清理待更新状态、启动健康确认。
        ///
        /// 必须在 OnStartup 里、_isPostUpdateBoot 被赋值之后调用。原先这段写在构造函数里，
        /// 而 WPF 的启动顺序是「构造函数 → Run() → OnStartup」，条件恒为 false：
        /// 于是 UpdateHealthGuard.ConfirmHealthy 永远没人调，安装前写的回滚标记也就永远删不掉，
        /// watcher 150 秒后把刚装好的新版本杀掉并用备份覆盖回去。
        /// </summary>
        private void HandlePostUpdateBoot()
        {
            try
            {
                WriteDiagnosticLog("[Update] 更新后启动");

                // 先核对：安装器报告装完了，但程序版本号没变 —— 说明这次安装其实没生效
                // （装到了别的目录、被安全软件拦下、或者被回滚了）。这时必须记进 FailedUpdateVersion，
                // 否则下一轮又会自动重装一遍，用户看到的就是「一直在更新，版本却始终是老的」。
                var expectedVersion = UpdateChecker.NormalizeVersion(_config.PendingUpdateVersion);
                var currentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                var updateEffective = string.IsNullOrWhiteSpace(expectedVersion)
                    || UpdateChecker.CompareVersions(currentVersion, expectedVersion) >= 0;

                if (updateEffective)
                {
                    _trayIcon?.SetStatusText($"已更新到 v{CurrentAppVersion}");
                }
                else
                {
                    WriteDiagnosticLog($"[Update] 更新未生效：期望 {expectedVersion}，实际仍是 {currentVersion}；"
                        + $"程序路径={System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}；"
                        + "已停止自动重试，请手动运行安装包完成升级。");
                    _config.FailedUpdateVersion = expectedVersion;
                    _trayIcon?.SetStatusText($"v{expectedVersion} 更新未生效，请手动安装");
                }

                // 安装器已经完成文件替换，清掉待安装状态，避免下次启动重复拉起安装器。
                if (!string.IsNullOrWhiteSpace(_config.PendingUpdateStage))
                {
                    _config.PendingUpdatePath = "";
                    _config.PendingUpdateSha256 = "";
                    _config.PendingUpdateVersion = "";
                    _config.PendingUpdateStage = "";
                    // 只有版本确实到位了才清掉「曾经装失败」的记录；没到位就留着，防止再次自动安装
                    if (updateEffective) _config.FailedUpdateVersion = "";
                    SaveConfig();
                }

                // 60 秒后确认网关健康并撤掉回滚保护。这是 ConfirmHealthy 唯一的调用点。
                _ = ConfirmUpdateHealthAsync();
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Update] 更新后收尾异常: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                var window = new ManagementWindow();
                // 「关闭时最小化到托盘」：拦下关闭动作改成隐藏。窗口对象留着复用 ——
                // 重建一个窗口要重新拉起 WebView2，代价远高于 Hide。
                window.Closing += (s, ev) =>
                {
                    if (_isExiting || !_config.MinimizeToTray) return;
                    ev.Cancel = true;
                    window.Hide();
                };
                // 窗口真被关掉之后就不能再 Show（WPF 语义），引用必须清掉。
                // 原先只在 MinimizeToTray 为真时清：关掉该设置的人从托盘再点「打开管理窗口」
                // 只会得到一个「无法显示主窗口」的报错框。
                window.Closed += (s, ev) => { if (!_isExiting) _mainWindow = null; };
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
            // 退出时按设置恢复 hosts：本程序不在运行时，local.id.seewo.com 不应继续指向本机。
            try
            {
                if (_config?.RestoreHostsOnExit != false && !_skipHostsRestoreOnExit)
                {
                    if (Services.HostsFileService.RemoveLoopbackMapping(out var hostsError))
                        WriteDiagnosticLog("[Hosts] 退出时已恢复 hosts 映射");
                    else
                        WriteDiagnosticLog($"[Hosts] 退出时恢复 hosts 失败（可能需要管理员权限）: {hostsError}");
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Hosts] 退出时恢复 hosts 异常: {ex.GetType().Name} - {ex.Message}");
            }

            _dailyTokenRefreshTimer?.Dispose();
            _qrLoginCoordinator?.Dispose();
            _qrLoginClient?.Dispose();
            _gateway?.Dispose();
            _authService?.Dispose();
            _userListRotation?.Dispose();
            _trayIcon?.Dispose();
            // 唤醒监听：不撤销的话那个后台线程会每秒轮询直到进程结束
            try { _instanceSignalCts?.Cancel(); } catch { }
            try { _instanceSignal?.Dispose(); } catch { }
            try { _instanceSignalCts?.Dispose(); } catch { }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }




        /// <summary>
        /// 重启应用（以管理员身份）
        /// </summary>
        internal void RestartApp()
        {
            var started = false;
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    // 先放开单实例互斥锁：新进程起来要几百毫秒，而本进程还要走完 Shutdown 才退出。
                    // 锁不放的话，新实例会判定「已有实例在运行」，把自己关掉、再去唤醒一个正在退出的旧实例 ——
                    // 用户看到的是「点了重启程序，它反而消失了」。
                    _instanceMutex?.Dispose();
                    _instanceMutex = null;

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
                // 启动不成功时保持当前实例继续运行，避免托盘常驻程序“凭空消失”。
                // 互斥锁刚才已经放开，这里必须拿回来，否则双击图标会开出第二个实例。
                try { _instanceMutex = new Mutex(true, InstanceMutexName, out _); } catch { }
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

        /// <summary>
        /// 静默升级专用退出：安装器已经拉起，这里先把配置落盘、停掉 SSO 网关，再结束进程，
        /// 让它能替换掉正在运行的程序文件（托盘图标与其余资源由 OnExit 统一释放）。
        /// 不弹确认框、不自动重启：新版本由用户（或下次开机自启）启动。
        /// </summary>
        internal void BeginSilentUpdateExit()
        {
            if (_isExiting) return;

            // 安装期间保持 hosts 映射：新版本启动后会重新确认映射并接管 24300，
            // 希沃这边的下一次 SSOLOGIN 请求就能正常拿到账号列表。
            _skipHostsRestoreOnExit = true;
            WriteDiagnosticLog("[Update] 静默安装已启动，程序即将退出以完成更新");
            try { SaveConfig(); }
            catch (Exception ex) { WriteDiagnosticLog($"[Update] 退出前保存配置失败: {ex.Message}"); }
            try { _gateway?.Stop(); }
            catch (Exception ex) { WriteDiagnosticLog($"[Update] 退出前停止 SSO 网关失败: {ex.Message}"); }

            _isExiting = true;
            try { _mainWindow?.Close(); } catch { }
            Shutdown();
        }

        /// <summary>
        /// 更新后健康确认：60 秒内确认网关可用就撤掉回滚保护。
        ///
        /// 判据必须排除「本来就不该起来」的情况：非管理员运行时网关启动失败是预期内的降级
        /// （用户在提权被拒时主动选了以降级模式继续用），不算新版本不健康 —— 否则这类机器每次升级
        /// 都会被 watcher 在超时后回滚，装上又被换回旧版。
        /// </summary>
        private async Task ConfirmUpdateHealthAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60));

                var expectedDowngrade = _gatewayDegradedDueToPermissions && !IsAdministrator();
                var healthy = (_gateway?.IsRunning == true || expectedDowngrade) && _config != null;
                if (healthy)
                {
                    if (expectedDowngrade && _gateway?.IsRunning != true)
                        WriteDiagnosticLog("[Update] 健康确认：非管理员运行，网关未启动属预期内降级，按健康处理");
                    Services.UpdateHealthGuard.ConfirmHealthy(WriteDiagnosticLog);
                }
                else
                {
                    WriteDiagnosticLog("[Update] 健康确认未通过：网关未监听，保留回滚保护");
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Update] 健康确认异常: {ex.GetType().Name} - {ex.Message}");
            }
        }




    }
}
