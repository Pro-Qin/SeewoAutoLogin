using System.Windows;

namespace SeewoAutoLogin
{
    public partial class App
    {
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

        /// <summary>安全模式：跳过 WebView2、遮罩与自动更新（连续启动失败后自动进入）。</summary>
        private bool _safeMode;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 启动流水线：每一步只做一件事，顺序本身就是约束。
            // 一次性命令要先于单实例判定（部署时旧实例可能还挂着）；
            // 提权要早于任何写注册表的动作；托盘要早于任何会写托盘提示的行为；
            // 首次引导要晚于网关启动，这样用户停在欢迎界面时希沃侧也已经可用。
            if (RunOneShotCommandIfRequested(e.Args)) { ExitAfterStartup(); return; }

            InstallCrashReporting(e.Args);

            if (!TryBecomePrimaryInstance(e.Args)) { ExitAfterStartup(); return; }

            if (!EnsureAdministrator(e.Args)) { ExitAfterStartup(); return; }

            PrepareRuntimeEnvironment();

            StartGatewayAndDetectSeewoVersion();

            StartCredentialKeepAlive();

            if (!ShowStartupUi(e.Args)) { ExitAfterStartup(); return; }

            StartBackgroundLoops();
        }

        /// <summary>启动流程的收尾退出：标记为「主动退出」，避免被当成最小化到托盘。</summary>
        private void ExitAfterStartup()
        {
            _isExiting = true;
            Shutdown();
        }

        /// <summary>
        /// 只做一件事就退出的启动模式。放在最前面：它们不该受单实例判定与提权流程影响。
        /// </summary>
        private bool RunOneShotCommandIfRequested(string[] args)
        {
            // 回滚 watcher 专用入口：不启动正常逻辑，只监控健康标记并在超时后回滚。
            if (args.Contains("--rollback-watch"))
            {
                Services.UpdateHealthGuard.RunRollbackWatch(args, WriteDiagnosticLog);
                return true;
            }

            // 无人值守部署：批量装到一体机时，一次把配置灌进去、开好开机自启就退出，不弹任何窗口。
            // 放在单实例判定之前 —— 部署时往往还有一个旧实例挂着，不能因为它在跑就把这次部署吞掉。
            if (args.Contains("--unattended"))
            {
                RunUnattendedSetup(args);
                return true;
            }

            return false;
        }

        /// <summary>注册全局异常处理，并读掉本次启动开关（安全模式、更新后启动、诊断参数）。</summary>
        private void InstallCrashReporting(string[] args)
        {
            Services.CrashReporter.Install(this, WriteDiagnosticLog);

            // 启动失败的计数放到真正接管机器之后（见 TryBecomePrimaryInstance），
            // 否则双击几次图标攒够 3 次就会被误判成连续失败、下次启动直接进安全模式。
            _safeMode = Services.CrashReporter.IsSafeModeRequested;
            if (_safeMode) WriteDiagnosticLog("[Crash] 本次启动进入安全模式：跳过 WebView2、遮罩与自动更新");

            // 安装器静默升级完成后会带 --updated 启动：用于跳过提权询问、自动接管 SSO 端口并清理待更新状态。
            _isPostUpdateBoot = args.Contains("--updated");

            // 排查 Win7 上的渲染残影（界面上偶尔飘过的深色小条）：--software-render 强制 WebView2 走
            // 软件渲染。正常启动看一次、带这个参数看一次，就能判断残影是不是 GPU 合成造成的。
            // 属于诊断开关，不写进设置界面。
            if (args.Contains("--software-render"))
            {
                Services.WebView2Runtime.ForceSoftwareRendering = true;
                WriteDiagnosticLog("[WebView2] 命令行指定强制软件渲染（--software-render）");
            }
        }

        /// <summary>
        /// 单实例接管与卸载清理。
        /// 返回 false 表示本次启动应当立即结束（已有实例接管，或卸载清理完成）。
        /// </summary>
        private bool TryBecomePrimaryInstance(string[] args)
        {
            // 卸载清理：setup.iss 的 UninstallRun 会调用 --uninstall。
            // 必须放在单实例判定之后执行，否则运行中的实例会把配置/日志重新写回，导致清理不干净。
            var isUninstall = args.Contains("--uninstall");

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
                // 已有实例在运行。
                //
                // 分两种情况：
                //  · 同一个版本的普通双击 → 唤醒它把窗口显示出来，本次退出（用户只是想看界面）。
                //  · 升级后启动（换了路径或版本更高）→ 结束旧实例，由本次启动接管。
                //    否则「覆盖安装完点新图标」会唤醒那个还没退出的旧进程，看起来像是升级没生效。
                if (TryTakeOverFromOlderInstance())
                {
                    instanceMutex?.Dispose();
                    try { instanceMutex = new Mutex(true, InstanceMutexName, out isFirstInstance); }
                    catch { isFirstInstance = true; }
                }
                else
                {
                    SignalExistingInstance();
                    instanceMutex?.Dispose();
                    return false;
                }
            }

            if (isUninstall)
            {
                try { CleanupForUninstall(); }
                catch (Exception ex) { WriteDiagnosticLog($"[Uninstall] 清理失败: {ex.Message}"); }
                return false;
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
            // 不该记进启动失败次数。
            Services.CrashReporter.MarkStartupAttempt(WriteDiagnosticLog);
            if (!_safeMode && Services.CrashReporter.IsSafeModeRequested)
            {
                _safeMode = true;
                WriteDiagnosticLog("[Crash] 本次启动进入安全模式：跳过 WebView2、遮罩与自动更新");
            }

            return true;
        }

        /// <summary>
        /// 判断正在运行的同名进程是不是「旧版本 / 别处的副本」。
        ///
        /// 只在能读到对方路径与版本时接管；读不到（权限不足）就保守地退回「唤醒」，
        /// 宁可多一次手动退出，也不要在没有把握时杀掉用户正在用的进程。
        /// </summary>
        private bool TryTakeOverFromOlderInstance()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                var currentPath = self.MainModule?.FileName;
                var currentVersion = typeof(App).Assembly.GetName().Version;
                if (string.IsNullOrEmpty(currentPath) || currentVersion == null) return false;

                var others = Process.GetProcessesByName("SeewoAutoLogin")
                    .Where(p => p.Id != self.Id)
                    .ToList();
                if (others.Count == 0) return false;

                var needTakeOver = false;
                foreach (var p in others)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (string.IsNullOrEmpty(path)) continue;

                        if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            needTakeOver = true;
                            break;
                        }

                        var raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
                        if (Version.TryParse(raw, out var other) && other < currentVersion)
                        {
                            needTakeOver = true;
                            break;
                        }
                    }
                    catch { }
                }

                if (!needTakeOver) return false;

                WriteDiagnosticLog("[Instance] 检测到旧版本实例，结束它并由本次启动接管");
                foreach (var p in others)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                        WriteDiagnosticLog($"[Instance] 已结束旧实例 pid={p.Id}");
                    }
                    catch (Exception ex)
                    {
                        WriteDiagnosticLog($"[Instance] 结束旧实例失败 pid={p.Id}: {ex.GetType().Name}");
                    }
                }
                Thread.Sleep(600);
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Instance] 版本接管判断失败（按唤醒处理）: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 管理员权限：快捷登录能否被希沃识别，取决于 hosts 映射与 HttpListener 的 URL ACL，两者都需要管理员。
        /// 返回 false 表示提权后本次启动应当结束（新进程会接管）。
        /// </summary>
        private bool EnsureAdministrator(string[] args)
        {
            if (args.Contains("--elevated"))
            {
                // 已带 --elevated：验证是否真的获得管理员权限（仅记录日志，不弹窗）
                if (!IsAdministrator())
                    WriteDiagnosticLog("[Elevate] 提权重启后仍未获得管理员权限，以降级模式运行");
                return true;
            }

            try
            {
                if (IsAdministrator()) return true;

                WriteDiagnosticLog("[Elevate] 非管理员权限，请求提权重启");
                if (TryElevate()) return false;

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
                    return false;
                }

                // 用户选择降级运行：降级模式下 SSO 可能不可用，记录日志
                WriteDiagnosticLog("[Elevate] 用户选择降级运行（SSO 快捷登录可能不可用）");
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Elevate] 权限检测失败，以当前权限继续运行: {ex.Message}");
                return true;
            }
        }

        /// <summary>与希沃无关的本机环境准备：TLS、托盘、更新后收尾、WebView 预热、开机自启自愈。</summary>
        private void PrepareRuntimeEnvironment()
        {
            // Win7/8.1 上 HTTPS 能否跑通，取决于系统 Schannel 有没有启用 TLS 1.2 客户端：
            // 程序保持 ServicePointManager 的 SystemDefault（微软建议不要硬编码协议版本），
            // 改为把系统层的 TLS 1.2 打开（与微软 KB3140245 的指引一致）。
            // 写注册表需要管理员权限，所以放在提权判定之后。
            Services.NetworkCompat.EnsureTls12ForLegacyWindows(WriteDiagnosticLog);

            try
            {
                _trayIcon.Initialize();
                if (_safeMode) _trayIcon.SetStatusText("安全模式：已跳过界面与自动更新");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"托盘初始化失败: {ex.Message}");
            }

            // 更新后收尾放在托盘初始化之后：它要写托盘提示，也要在版本没到位时把话说清楚
            if (_isPostUpdateBoot) HandlePostUpdateBoot();

            // WebView2 预热：提前在后台创建运行时环境与浏览器进程，缩短主界面首次加载的等待
            // （托盘常驻场景同样预热，用户点开主界面时无需再等浏览器进程启动）
            if (!_safeMode) Services.WebView2Runtime.Prewarm(WriteDiagnosticLog);

            EnsureAutoStartRegistration();
        }

        /// <summary>开机自启自愈：配置要求自启、但系统里没有启动项（被杀软清理、程序换目录、旧版本从未写入）时自动重建。</summary>
        private void EnsureAutoStartRegistration()
        {
            try
            {
                if (!_config.AutoStartEnabled) return;

                var autoStartModeNow = Services.AutoStartService.CurrentMode();
                // 缺失或指向旧路径（换目录/被杀软清理）→ 重建
                var needsRebuild = !Services.AutoStartService.IsEnabledForCurrentPath();
                // 旧版本与安装包写入的是 HKCU 启动项（每次开机都要授权一次）；
                // 管理员运行时顺带升级为「最高权限计划任务」，实现开机免 UAC
                var needsUpgrade = autoStartModeNow == "registry" && Services.AutoStartService.IsAdministrator();
                if (!needsRebuild && !needsUpgrade) return;

                if (Services.AutoStartService.Enable(out var autoStartError, out var autoStartMode))
                    WriteDiagnosticLog(needsUpgrade
                        ? $"[AutoStart] 已将注册表启动项升级为计划任务; mode={autoStartMode}"
                        : $"[AutoStart] 自启缺失或指向旧路径，已按配置重建; mode={autoStartMode}");
                else
                    WriteDiagnosticLog($"[AutoStart] 自启重建失败: {autoStartError}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[AutoStart] 自启自检异常: {ex.Message}");
            }
        }

        /// <summary>启动本地 SSO 网关，并顺手查一次希沃客户端版本变化。</summary>
        private void StartGatewayAndDetectSeewoVersion()
        {
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
        }

        /// <summary>账号保活与网关是否可用无关：即便以普通权限运行、网关没能启动，也必须在凭据过期前续期。</summary>
        private void StartCredentialKeepAlive()
        {
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
        }

        /// <summary>
        /// 首次引导与主窗口。
        /// 返回 false 表示用户在欢迎界面拒绝协议，本次启动应当结束。
        /// </summary>
        private bool ShowStartupUi(string[] args)
        {
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
                        return false;
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
            var startMinimized = _safeMode || (!welcomeShown && (args.Contains("--minimized") || _config.StartMinimized));
            if (startMinimized)
            {
                // 同样只写悬停提示：开机自启恰好落在上课时间，启动弹通知是最容易被投诉的一种打扰
                try { _trayIcon?.SetStatusText(Strings.StartMinimized); } catch { }
                // 主界面不会自动打开：桌面提醒是用户唯一能直接看到「已经升级」的入口
                if (!_safeMode) _ = ShowDesktopUpgradeNoticeIfNeededAsync();
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

            return true;
        }

        /// <summary>启动后台任务：静默更新、启动成功标记、自动修复巡检。</summary>
        private void StartBackgroundLoops()
        {
            // ClassIsland 同款静默更新：后台检查、自动下载、静默安装，不依赖主界面是否打开。
            if (!_safeMode && _config.AutoCheckUpdate && _updateCoordinator != null)
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
        /// 无人值守部署：--unattended [--config 配置文件路径] [--no-autostart]
        ///
        /// 面向学校批量部署（Intune / SCCM / 组策略脚本）。它只做三件事：导入配置、
        /// 打开开机自启、写一条结果日志，然后退出 —— 全程不弹窗，退出码 0/1 供脚本判断。
        /// </summary>
        private void RunUnattendedSetup(string[] args)
        {
            var ok = true;
            try
            {
                var configPath = GetArgValue(args, "--config");
                if (!string.IsNullOrWhiteSpace(configPath))
                {
                    if (File.Exists(configPath))
                    {
                        ok &= ImportConfig(configPath);
                        WriteDiagnosticLog($"[Unattended] 已导入配置：{configPath}（{(ok ? "成功" : "失败")}）");
                    }
                    else
                    {
                        ok = false;
                        WriteDiagnosticLog($"[Unattended] 配置文件不存在：{configPath}");
                    }
                }

                if (!args.Contains("--no-autostart"))
                {
                    if (Services.AutoStartService.Enable(out var error, out var mode))
                        WriteDiagnosticLog($"[Unattended] 已启用开机自启（{mode}）");
                    else
                    {
                        ok = false;
                        WriteDiagnosticLog($"[Unattended] 启用开机自启失败：{error}");
                    }
                }

                WriteDiagnosticLog(ok ? "[Unattended] 部署完成" : "[Unattended] 部署未完全成功，请看上面的日志");
            }
            catch (Exception ex)
            {
                ok = false;
                WriteDiagnosticLog($"[Unattended] 部署异常: {ex.GetType().Name} - {ex.Message}");
            }

            Environment.ExitCode = ok ? 0 : 1;
        }

        private static string GetArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

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
                var last = SeewoSsoGateway.LastSsologinUtc;
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
    }
}
