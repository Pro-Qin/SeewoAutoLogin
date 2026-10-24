using System.Security.Principal;
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
            // 界面文案：早于任何窗口创建，之后所有 Strings / I18n.T 都拿得到值。
            // 语言包缺失时 T() 返回键名，界面不会空白，也不会阻断启动。
            I18n.Initialize();

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
            //
            // 只有真正接管了这台机器的主实例才做这件事（_instanceMutex 非空）。
            // 否则「双击图标唤醒已有实例」的那条路径也会在退出时删映射：它自己没写过 hosts，
            // 却会把正在运行的主实例刚写好的映射删掉 —— 若唤醒者是管理员，快捷登录当场失效。
            // 重启/静默升级的路径已经把互斥锁置空，同样不会误删。
            try
            {
                if (_instanceMutex != null && _config?.RestoreHostsOnExit != false && !_skipHostsRestoreOnExit)
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
