using Microsoft.Web.WebView2.Core;
using SeewoAutoLogin.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        public ManagementWindow()
        {
            InitializeComponent();
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
            try
            {
                await WebView.EnsureCoreWebView2Async();
                WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                WebView.CoreWebView2.Settings.IsScriptEnabled = true;
                WebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
                WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                WebView.CoreWebView2.DOMContentLoaded += OnDomContentLoaded;

                // 组合前端资源：HTML + CSS + JS
                var html = GetEmbedded("SeewoAutoLogin.frontend.index.html");
                var css = GetEmbedded("SeewoAutoLogin.frontend.styles.css");
                var js = GetEmbedded("SeewoAutoLogin.frontend.app.js");

                // 构建完整 HTML
                var fullHtml = html.Replace("</head>", "<style>" + css + "</style></head>");
                fullHtml = fullHtml.Replace("</body>", "<script>" + js + "</script></body>");

                WebView.CoreWebView2.NavigateToString(fullHtml);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ManagementWindow] 初始化失败: {ex.Message}");
                MessageBox.Show($"WebView2 初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnDomContentLoaded(object sender, object e)
        {
            _webViewReady = true;
            await SendToJs(new
            {
                type = "init",
                accounts = GetAccountList(),
                needsPassword = _app.Config.UsePluginPassword && !string.IsNullOrEmpty(_app.Config.PluginPasswordHash)
            });
            // 同时推送设置状态和希沃状态
            await SendSettings();
            await SendSeewoStatus();
            UpdateGatewayStatus();
            StartSeewoMonitor();
        }

        #region Embedded Resource

        private string GetEmbedded(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            var full = asm.GetManifestResourceNames().FirstOrDefault(n => n == name || n.EndsWith(name));
            if (full == null) throw new InvalidOperationException($"Embedded resource not found: {name}");
            using var s = asm.GetManifestResourceStream(full);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        #endregion

        #region C# → JS

        private Task SendToJs(object msg)
        {
            if (!_webViewReady) return Task.CompletedTask;
            try
            {
                WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
            }
            catch { }
            return Task.CompletedTask;
        }

        #endregion

        #region JS → C# (Message Handler)

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json)) return;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                // 解锁前只放行 unlock，其余消息（含托盘可触达的配置变更）全部忽略
                if (type != "unlock" && IsLocked) return;

                switch (type)
                {
                    case "login": await HandleLogin(root); break;
                    case "start-qr": _ = StartQrLoginAsync(); break;
                    case "cancel-qr": _qrLoginCancellation?.Cancel(); _qrLoginCoordinator?.Cancel(); break;
                    case "delete-account": HandleDeleteAccount(root); break;
                    case "set-active": HandleSetActive(root); break;
                    case "move-up": HandleMove(root, -1); break;
                    case "move-down": HandleMove(root, 1); break;
                    case "edit-tags": HandleEditTags(root); break;
                    case "edit-display-name": HandleEditDisplayName(root); break;
                    case "export-config": HandleExportConfig(); break;
                    case "import-config": HandleImportConfig(); break;
                    case "open-log-viewer": new LogViewerWindow { Owner = this }.ShowDialog(); break;
                    case "open-diagnostic": new DiagnosticWindow { Owner = this }.ShowDialog(); break;
                    case "restart": _app.RestartApp(); break;
                    case "exit": _app.BeginExit(); break;
                    case "set-password": HandleSetPassword(root); break;
                    case "clear-password": HandleClearPassword(); break;
                    case "update-setting": HandleUpdateSetting(root); break;
                    case "unlock": HandleUnlock(root); break;
                    case "add-fake-account": await HandleAddFakeAccount(root); break;
                    case "refresh-debug": await SendSeewoStatus(); break;
                    case "move-to-active": HandleMoveToActive(root); break;
                    case "move-to-inactive": HandleMoveToInactive(root); break;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[WebView] msg error: {ex.Message}"); }
        }

        #endregion

        #region Handlers

        private async Task HandleLogin(JsonElement root)
        {
            var username = root.GetProperty("username").GetString() ?? "";
            var password = root.GetProperty("password").GetString() ?? "";
            var displayName = root.GetProperty("displayName").GetString() ?? "";
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            { await SendToJs(new { type = "login-status", text = "请输入账号和密码" }); return; }

            _passwordLoginCancellation?.Cancel(); _passwordLoginCancellation?.Dispose();
            _passwordLoginCancellation = new CancellationTokenSource();
            var ct = _passwordLoginCancellation;
            try
            {
                await SendToJs(new { type = "login-status", text = "正在验证..." });
                var svc = new SeewoAuthService();
                var result = await svc.LoginAsync(username, password, ct.Token);
                if (result.Success)
                {
                    var acct = new SeewoAccount
                    {
                        DisplayName = string.IsNullOrEmpty(displayName) ? (result.UserInfo?.NickName ?? username) : displayName,
                        Username = username, Password = password, UserInfo = result.UserInfo
                    };
                    _app.AddAccount(acct);
                    await SendToJs(new { type = "login-status", text = "登录成功，已保存" });
                    await Task.Delay(600);
                    await RefreshAccountList();
                }
                else await SendToJs(new { type = "login-status", text = $"登录失败: {result.ErrorMessage}" });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await SendToJs(new { type = "login-status", text = $"登录失败: {ex.Message}" }); }
            finally { if (ReferenceEquals(_passwordLoginCancellation, ct)) { _passwordLoginCancellation.Dispose(); _passwordLoginCancellation = null; } }
        }

        private async void HandleDeleteAccount(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            if (MessageBox.Show($"确定删除 \"{acct.DisplayName ?? acct.Username}\" 吗？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _app.RemoveAccount(id);
            await RefreshAccountList();
        }

        private async void HandleSetActive(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            if (!string.IsNullOrEmpty(id)) { _app.SwitchActiveAccount(id); await RefreshAccountList(); }
        }

        private async void HandleMove(JsonElement root, int dir)
        {
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts; var idx = list.FindIndex(a => a.Id == id);
            if (idx < 0) return;
            var ni = idx + dir;
            if (ni < 0 || ni >= list.Count) return;
            var item = list[idx]; list.RemoveAt(idx); list.Insert(ni, item);
            _app.SaveConfig(); await RefreshAccountList();
        }

        private async void HandleEditTags(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            var tags = root.GetProperty("tags").GetString() ?? "";
            if (string.IsNullOrEmpty(id)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            acct.Tags = tags.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
            _app.SaveConfig(); await RefreshAccountList();
        }

        private async void HandleEditDisplayName(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            var name = root.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(name)) return;
            var acct = _app.Config.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct == null) return;
            acct.DisplayName = name.Trim(); _app.SaveConfig(); await RefreshAccountList();
        }

        private async void HandleSetPassword(JsonElement root)
        {
            var pw = root.GetProperty("password").GetString() ?? "";
            if (string.IsNullOrEmpty(pw)) return;
            var salt = GenerateSalt(); var hash = ComputeHash(pw, salt);
            _app.Config.PluginPasswordHash = hash; _app.Config.PluginPasswordSalt = salt; _app.SaveConfig();
            await SendToJs(new { type = "settings", passwordSet = true });
        }

        private async void HandleClearPassword()
        {
            _app.Config.PluginPasswordHash = ""; _app.Config.PluginPasswordSalt = ""; _app.SaveConfig();
            await SendToJs(new { type = "settings", passwordSet = false });
        }

        private async void HandleUnlock(JsonElement root)
        {
            var pw = root.GetProperty("password").GetString() ?? "";
            if (string.IsNullOrEmpty(pw)) { await SendToJs(new { type = "unlock-status", text = "请输入密码" }); return; }
            var ok = _app.Config.UsePluginPassword && !string.IsNullOrEmpty(_app.Config.PluginPasswordHash)
                && VerifyPassword(pw, _app.Config.PluginPasswordHash, _app.Config.PluginPasswordSalt);
            if (ok) { _unlocked = true; await SendToJs(new { type = "unlock-success" }); await SendSettings(); }
            else await SendToJs(new { type = "unlock-status", text = "密码错误" });
        }

        private void HandleUpdateSetting(JsonElement root)
        {
            var key = root.GetProperty("key").GetString();
            var val = root.GetProperty("value");
            switch (key)
            {
                case "usePluginPassword": _app.Config.UsePluginPassword = val.GetBoolean(); break;
                case "userListRotationEnabled": _app.Config.UserListRotationEnabled = val.GetBoolean(); break;
                case "userListRotationGroupSize": _app.Config.UserListRotationGroupSize = SeewoUserListRotationService.NormalizeGroupSize(val.GetInt32()); break;
                case "minimizeToTray": _app.Config.MinimizeToTray = val.GetBoolean(); break;
                case "startMinimized": _app.Config.StartMinimized = val.GetBoolean(); break;
                case "autoStart": if (val.GetBoolean()) AutoStartService.Enable(); else AutoStartService.Disable(); break;
            }
            _app.SaveConfig();
        }

        private void HandleExportConfig()
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = "导出配置", Filter = "配置文件 (*.json)|*.json", FileName = $"SeewoAutoLogin_config_{DateTime.Now:yyyyMMdd}.json" };
            if (d.ShowDialog() == true) { _app.ExportConfig(d.FileName); _app.TrayIcon?.SetStatusText("配置已导出"); }
        }

        private async Task HandleAddFakeAccount(JsonElement root)
        {
            var displayName = root.GetProperty("displayName").GetString() ?? "测试用户";
            var fakeId = "FAKE_" + Guid.NewGuid().ToString("N")[..6];
            var fakeAccount = new SeewoAccount
            {
                Id = fakeId,
                DisplayName = displayName,
                Username = $"fake_{fakeId.ToLower()}",
                Password = "fake_password_that_will_fail"
            };
            _app.Config.Accounts.Add(fakeAccount);
            _app.SaveConfig();
            await RefreshAccountList();
            await SendToJs(new { type = "login-status", text = $"已添加假账号: {displayName}" });
        }

        private async void HandleMoveToActive(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts;
            var idx = list.FindIndex(a => a.Id == id);
            if (idx < PluginConfig.MaxVisibleAccounts) return; // 已经在生效区
            var acct = list[idx];
            list.RemoveAt(idx);
            // 插入到生效区最后一个
            list.Insert(PluginConfig.MaxVisibleAccounts - 1, acct);
            _app.SaveConfig();
            await RefreshAccountList();
        }

        private async void HandleMoveToInactive(JsonElement root)
        {
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return;
            var list = _app.Config.Accounts;
            var idx = list.FindIndex(a => a.Id == id);
            if (idx < 0 || idx >= PluginConfig.MaxVisibleAccounts) return; // 不在生效区
            var acct = list[idx];
            list.RemoveAt(idx);
            // 插入到生效区边界，生效区减一，不自动补位
            list.Insert(Math.Min(PluginConfig.MaxVisibleAccounts, list.Count), acct);
            _app.SaveConfig();
            await RefreshAccountList();
        }

        private void HandleImportConfig()
        {
            if (MessageBox.Show("导入配置将覆盖当前所有设置，确定？", "导入", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var d = new Microsoft.Win32.OpenFileDialog { Title = "导入配置", Filter = "配置文件 (*.json)|*.json" };
            if (d.ShowDialog() == true && _app.ImportConfig(d.FileName)) { _app.TrayIcon?.SetStatusText("配置已导入"); _app.RestartApp(); }
        }

        #endregion

        #region Account List

        private async Task RefreshAccountList()
        {
            await SendToJs(new { type = "account-list", accounts = GetAccountList() });
        }

        private object GetAccountList()
        {
            return _app.Config.Accounts.Select(a => new
            {
                id = a.Id,
                displayName = a.DisplayName ?? a.Username ?? "",
                username = a.Username ?? "",
                loginType = string.IsNullOrEmpty(a.Password) ? "扫码" : "密码",
                isActive = a.Id == _app.Config.ActiveAccountId,
                initial = (a.DisplayName ?? a.Username ?? "S").Substring(0, 1).ToUpperInvariant(),
                tags = a.Tags != null && a.Tags.Count > 0 ? string.Join(", ", a.Tags) : "",
                requestCount = a.RequestCount,
                lastRequestAtUtc = a.LastRequestAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""
            }).ToList();
        }

        /// <summary>网关向希沃返回账号列表后，由 App 调用以刷新计数展示</summary>
        internal void NotifyAccountsServed()
        {
            if (!_webViewReady) return;
            _ = RefreshAccountList();
        }

        #endregion

        #region QR Login

        private async void QrLoginCoordinator_StateChanged(object sender, QrLoginStateChangedEventArgs e)
        {
            await Dispatcher.BeginInvoke(new Action(async () => await RenderQrState(e)));
        }

        private async Task RenderQrState(QrLoginStateChangedEventArgs e)
        {
            var active = e.State is QrLoginState.CreatingQrCode or QrLoginState.WaitingForScan or QrLoginState.WaitingForConfirmation or QrLoginState.Completing;
            var s = e.State switch
            {
                QrLoginState.CreatingQrCode => "creating", QrLoginState.WaitingForScan => "waiting-scan",
                QrLoginState.WaitingForConfirmation => "waiting-confirm", QrLoginState.Completing => "completing",
                QrLoginState.Succeeded => "succeeded", QrLoginState.Expired => "expired",
                QrLoginState.Cancelled => "cancelled", QrLoginState.Denied => "denied",
                QrLoginState.NetworkError => "network-error", QrLoginState.ProtocolError => "protocol-error", _ => "idle"
            };
            string imgData = null;
            if (e.Session?.ImageBytes.Length > 0) { imgData = "data:image/png;base64," + Convert.ToBase64String(e.Session.ImageBytes); _qrExpiresAt = e.Session.ExpiresAt; _qrCountdownTimer.Start(); }
            var text = e.State switch
            {
                QrLoginState.CreatingQrCode => "正在创建二维码...", QrLoginState.WaitingForScan => "请使用希沃App扫码",
                QrLoginState.WaitingForConfirmation => "请在手机上确认", QrLoginState.Completing => "正在登录...",
                QrLoginState.Succeeded => "登录成功！", QrLoginState.Expired => "二维码已过期",
                QrLoginState.Cancelled => "已取消", QrLoginState.Denied => "已拒绝",
                QrLoginState.NetworkError => "网络错误", QrLoginState.ProtocolError => string.IsNullOrWhiteSpace(e.Message) ? "协议错误" : e.Message, _ => ""
            };
            if (e.State == QrLoginState.Succeeded) _qrCountdownTimer.Stop();
            await SendToJs(new { type = "qr-state", state = s, imageData = imgData, text });
            if (!active) { _qrCountdownTimer.Stop(); await SendToJs(new { type = "qr-countdown", text = "" }); }
        }

        private void QrCountdownTimer_Tick(object sender, EventArgs e)
        {
            var sec = Math.Max(0, (int)Math.Ceiling((_qrExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
            _ = SendToJs(new { type = "qr-countdown", text = $"{sec} 秒后过期" });
            if (sec == 0) _qrCountdownTimer.Stop();
        }

        private async Task StartQrLoginAsync()
        {
            _qrLoginCancellation?.Cancel(); _qrLoginCancellation?.Dispose();
            _qrLoginCancellation = new CancellationTokenSource();
            var ct = _qrLoginCancellation;
            try
            {
                var outcome = await _qrLoginCoordinator.StartAsync(ct.Token);
                if (outcome == null || ct.IsCancellationRequested) return;
                _authService.AcceptQrLogin(outcome);
                var uname = !string.IsNullOrWhiteSpace(outcome.UserInfo?.Phone) ? outcome.UserInfo.Phone : outcome.UserInfo?.UserName ?? "";
                var acct = new SeewoAccount
                {
                    DisplayName = outcome.UserInfo?.NickName ?? outcome.UserInfo?.RealName ?? "未命名",
                    Username = uname, Password = "", UserInfo = outcome.UserInfo
                };
                _app.AddQrAccount(acct, outcome);
                await RefreshAccountList();
                await SendToJs(new { type = "qr-state", state = "succeeded", text = "登录成功！" });
            }
            finally { if (ReferenceEquals(_qrLoginCancellation, ct)) { _qrLoginCancellation.Dispose(); _qrLoginCancellation = null; } }
        }

        #endregion

        #region Gateway Status

        private void UpdateGatewayStatus()
        {
            if (_app?.Gateway == null) return;
            _ = SendToJs(new { type = "gateway-status", running = _app.Gateway.IsRunning });
        }

        #endregion

        #region Settings

        private async Task SendSettings()
        {
            await SendToJs(new
            {
                type = "settings",
                passwordEnabled = _app.Config.UsePluginPassword,
                passwordSet = !string.IsNullOrEmpty(_app.Config.PluginPasswordHash),
                rotationEnabled = _app.Config.UserListRotationEnabled,
                rotationGroupSize = SeewoUserListRotationService.NormalizeGroupSize(_app.Config.UserListRotationGroupSize),
                autoStart = AutoStartService.IsEnabled,
                minimizeToTray = _app.Config.MinimizeToTray,
                startMinimized = _app.Config.StartMinimized
            });
        }

        private async Task SendSeewoStatus()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessesByName("EasiNote").FirstOrDefault();
                bool running = proc != null;
                bool loggedIn = false;
                string hwndStr = "";
                string windowTitle = "";
                var rect = new { w = 0, h = 0 };

                if (running && proc.MainWindowHandle != IntPtr.Zero)
                {
                    hwndStr = proc.MainWindowHandle.ToString();
                    windowTitle = proc.MainWindowTitle;
                    User32.GetWindowRect(proc.MainWindowHandle, out RECT r);
                    rect = new { w = r.Right - r.Left, h = r.Bottom - r.Top };
                    loggedIn = _app.Config.Accounts.Any(a => _app.AuthService != null && _app.AuthService.IsSessionFor(a));
                    // 如果希沃长时间没请求 SSO（>5秒），认为已退出登录
                    if (loggedIn && _app.Gateway?.LastSsoRequestAtUtc != null)
                    {
                        var idle = DateTime.UtcNow - _app.Gateway.LastSsoRequestAtUtc.Value;
                        if (idle.TotalSeconds > 5) loggedIn = false;
                    }
                }

                await SendToJs(new
                {
                    type = "seewo-status",
                    running,
                    loggedIn,
                    hwnd = hwndStr,
                    title = windowTitle,
                    width = rect.w,
                    height = rect.h,
                    accounts = _app.Config.Accounts.Count,
                    active = Math.Min(6, _app.Config.Accounts.Count),
                    lastRefresh = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                await SendToJs(new { type = "seewo-status", running = false, error = ex.Message });
            }
        }

        private System.Windows.Threading.DispatcherTimer _seewoMonitorTimer;
        private void StopSeewoMonitor()
        {
            if (_seewoMonitorTimer != null)
            {
                _seewoMonitorTimer.Stop();
                _seewoMonitorTimer = null;
            }
        }

        private void StartSeewoMonitor()
        {
            StopSeewoMonitor(); // 防重复开启：窗口每次加载都重建，避免叠加多个 2s 轮询
            _seewoMonitorTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _seewoMonitorTimer.Tick += async (s, e) =>
            {
                await SendSeewoStatus();

                // 自动遮罩逻辑
                var proc = System.Diagnostics.Process.GetProcessesByName("EasiNote").FirstOrDefault();
                bool running = proc != null && proc.MainWindowHandle != IntPtr.Zero;
                bool loggedIn = false;
                if (running)
                    loggedIn = _app.Config.Accounts.Any(a => _app.AuthService != null && _app.AuthService.IsSessionFor(a));

                if (running && !loggedIn)
                {
                    // 希沃打开但未登录 → 自动显示遮罩
                    if (_app.CurrentOverlay == null || !_app.CurrentOverlay.IsVisible)
                        _app.ToggleOverlay();
                }
                else if (!running || loggedIn)
                {
                    // 希沃关闭或已登录 → 关闭遮罩
                    if (_app.CurrentOverlay != null && _app.CurrentOverlay.IsVisible)
                        _app.CurrentOverlay.Close();
                }
            };
            _seewoMonitorTimer.Start();
        }

        #endregion

        #region Helpers

        private static string GenerateSalt()
        {
            var b = new byte[32];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(b); return Convert.ToBase64String(b);
        }

        private static string ComputeHash(string pw, string salt)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(pw + salt)));
        }

        private static bool VerifyPassword(string pw, string hash, string salt) => ComputeHash(pw, salt) == hash;

        #endregion
    }
}
