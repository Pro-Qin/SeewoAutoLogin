using Microsoft.Web.WebView2.Core;
using SeewoAutoLogin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        private readonly App _app;
        private readonly DispatcherTimer _qrCountdownTimer;
        private readonly QrLoginCoordinator _qrLoginCoordinator;
        private readonly SeewoAuthService _authService;
        private CancellationTokenSource _passwordLoginCancellation;
        private CancellationTokenSource _qrLoginCancellation;
        private DateTimeOffset _qrExpiresAt;
        private bool _webViewReady;
        private bool _unlocked;
        private bool _webViewInitialized;
        private bool _autoInstallAttempted;
        private bool _skipIntro;
        private readonly TaskCompletionSource<bool> _contentReady =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManagementWindow()
        {
            InitializeComponent();
            // 显式指定窗口图标：只用 exe 图标时，任务栏会沿用 Windows 的旧图标缓存，
            // 换图标后用户看到的仍是旧的。从嵌入资源直接加载可确保与安装包一致。
            try { Icon = App.LoadWindowIcon(); } catch { }
            _app = (App)Application.Current;
            _qrLoginCoordinator = _app?.QrLoginCoordinator;
            _authService = _app?.AuthService;

            if (_qrLoginCoordinator != null)
                _qrLoginCoordinator.StateChanged += QrLoginCoordinator_StateChanged;
            _qrCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _qrCountdownTimer.Tick += QrCountdownTimer_Tick;

            Loaded += OnLoaded;
            Closed += (s, e) => StopSeewoMonitor();
        }

        /// <summary>密码保护启用且尚未解锁时，除 unlock 外的 WebView 消息一律忽略</summary>
        private bool IsLocked =>
            _app.Config.UsePluginPassword &&
            !string.IsNullOrEmpty(_app.Config.PluginPasswordHash) &&
            !_unlocked;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded; // 窗口重新显示时不重复初始化

            // 开场动画与 WebView2 初始化并行：动画遮罩先盖住窗口，界面在背后加载
            var intro = PlayIntroIfNeededAsync();
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                _app?.WriteDiagnosticLog($"[WebView2] 初始化失败: {ex}");
                ShowStatusPanel("界面初始化失败",
                    $"WebView2 初始化失败：{ex.Message}\n\n可点击「重新检测」重试，或「查看日志」了解详情。",
                    showRetry: true, showInstall: !WebView2Runtime.IsInstalled, showDownload: true);
            }

            await intro;
        }




        private async void OnDomContentLoaded(object sender, object e)
        {
            if (_webViewReady) return;
            _webViewReady = true;
            _contentReady.TrySetResult(true);
            HideStatusPanel();

            await SendToJs(new
            {
                type = "init",
                version = AppVersion,
                accounts = GetAccountList(),
                needsPassword = _app.Config.UsePluginPassword && !string.IsNullOrEmpty(_app.Config.PluginPasswordHash),
                config = new
                {
                    maxVisibleAccounts = PluginConfig.MaxVisibleAccounts,
                    autoStartText = AutoStartService.DescribeState(),
                    updateSource = _app.Config.UpdateSource ?? ""
                }
            });
            // 同时推送设置状态、自检状态和希沃状态
            await SendSettings();
            await SendStatus();
            await SendSeewoStatus();
            UpdateGatewayStatus();
            StartSeewoMonitor();

            // 希沃客户端版本变化提示（仅在检测到变化的那一次启动推送一次）
            var seewoVersionChange = _app?.SeewoVersionChange;
            if (seewoVersionChange != null && seewoVersionChange.Changed)
            {
                await SendToJs(new
                {
                    type = "seewo-version-changed",
                    from = seewoVersionChange.Previous ?? "",
                    to = seewoVersionChange.Current ?? ""
                });
            }

            // 首次使用引导：欢迎界面选择了「查看教程」时自动播放（等界面渲染稳定再开始）
            if (_app.Config.PendingTour)
            {
                await Task.Delay(700);
                await SendToJs(new { type = "start-tour" });
                _app.WriteDiagnosticLog("[Tour] 已自动播放使用教程");
            }

            // 启动时自动检查更新（仅一次，失败静默）
            if (_app.Config.AutoCheckUpdate) await HandleCheckUpdateAsync(manual: false);
        }

        private static string AppVersion
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "1.8.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }










    }
}
