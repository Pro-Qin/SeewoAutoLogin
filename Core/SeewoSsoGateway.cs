using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin
{
    public class SeewoSsoGateway : IDisposable
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly SeewoAuthService _authService;
        private readonly Func<PluginConfig> _getConfig;
        private readonly Func<SeewoAccount, bool> _tryRestoreQrSession;
        private readonly Func<IReadOnlyList<SeewoAccount>> _getVisibleAccounts;
        private readonly Action<SeewoAccount> _onLoginSuccess;
        private readonly Action<SeewoAccount, string> _onQrTokenValidated;
        private readonly SeewoUserListRotationService _userListRotation;
        private readonly Action _saveConfig;
        private DateTime _lastConfigSaveUtc;
        /// <summary>最近一次希沃发起 SSO 请求的时间（UTC），用于检测希沃是否还活跃</summary>
        public DateTime? LastSsoRequestAtUtc { get; private set; }

        public int Port { get; set; } = 24300;
        public bool IsRunning => _listener?.IsListening == true;

        public SeewoSsoGateway(
            SeewoAuthService authService,
            Func<PluginConfig> getConfig,
            Func<SeewoAccount, bool> tryRestoreQrSession,
            Func<IReadOnlyList<SeewoAccount>> getVisibleAccounts,
            Action<SeewoAccount> onLoginSuccess,
            Action<SeewoAccount, string> onQrTokenValidated,
            SeewoUserListRotationService userListRotation,
            Action saveConfig = null)
        {
            _authService = authService;
            _getConfig = getConfig;
            _tryRestoreQrSession = tryRestoreQrSession;
            _getVisibleAccounts = getVisibleAccounts;
            _onLoginSuccess = onLoginSuccess;
            _onQrTokenValidated = onQrTokenValidated;
            _userListRotation = userListRotation;
            _saveConfig = saveConfig;
        }

        /// <summary>请求统计变更后节流落盘，避免每次 SSO 请求都写配置</summary>
        private void ThrottledSaveConfig()
        {
            if (_saveConfig == null) return;
            var now = DateTime.UtcNow;
            if ((now - _lastConfigSaveUtc).TotalSeconds < 5) return;
            _lastConfigSaveUtc = now;
            try { _saveConfig(); }
            catch (Exception ex) { Log($"SSO 请求统计落盘失败: {ex.GetType().Name}"); }
        }

        private const string SeeSoLocalHost = "local.id.seewo.com";
        private const string TrustedEasiAgentSuffix = @"\Seewo\EasiAgent\EasiAgent.exe";

        public void Start()
        {
            if (IsRunning) return;

            EnsureHostsMapping();
            ResetListener();

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException) when (IsPortListening(Port))
            {
                Log($"SSO 网关端口 {Port} 已被占用，正在检查希沃 EasiAgent");
                if (!TryStopTrustedEasiAgent())
                    throw;

                if (!WaitForPortRelease(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException($"结束希沃 EasiAgent 后端口 {Port} 未及时释放。");

                ResetListener();
                _listener.Start();
                Log("已结束占用端口的希沃 EasiAgent，并重新加载 SSO 网关");
            }

            _ = Task.Run(() => ListenLoop(_cts.Token));
            Log($"SSO 网关已启动: http://localhost:{Port}");
        }

        private void ResetListener()
        {
            try { _listener?.Close(); } catch { }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try { _listener.Prefixes.Add($"http://{SeeSoLocalHost}:{Port}/"); }
            catch { }
        }

        private bool TryStopTrustedEasiAgent()
        {
            foreach (var process in Process.GetProcessesByName("EasiAgent"))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!IsTrustedEasiAgentPath(path))
                    {
                        Log($"拒绝结束非受信任路径的 EasiAgent; pid={process.Id}");
                        continue;
                    }
                    Log($"正在结束希沃 EasiAgent; pid={process.Id}");
                    process.Kill();
                    process.WaitForExit(5000);
                    return process.HasExited;
                }
                catch (Exception ex)
                {
                    Log($"结束希沃 EasiAgent 失败; pid={process.Id}; error={ex.GetType().Name}");
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }

        private static bool IsTrustedEasiAgentPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var fullPath = Path.GetFullPath(path);
                return fullPath.EndsWith(TrustedEasiAgentSuffix, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(Path.GetFileName(fullPath), "EasiAgent.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private bool WaitForPortRelease(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsPortListening(Port)) return true;
                Thread.Sleep(100);
            }
            return !IsPortListening(Port);
        }

        private static bool IsPortListening(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == port);
            }
            catch { return false; }
        }

        private static bool IsLocalOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host;
            return host == "localhost" || host == "127.0.0.1" ||
                   host == "::1" || host == "[::1]" || host == SeeSoLocalHost;
        }

        private static void EnsureHostsMapping()
        {
            try
            {
                var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "drivers", "etc", "hosts");
                if (!File.Exists(hostsPath)) return;
                var content = File.ReadAllText(hostsPath);
                if (content.Contains(SeeSoLocalHost)) return;
                File.AppendAllText(hostsPath,
                    Environment.NewLine + "127.0.0.1 " + SeeSoLocalHost + Environment.NewLine);
            }
            catch { }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
            Log("SSO 网关已停止");
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (HttpListenerException) { break; }
                catch { }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";

            // CORS：只放行本机来源（localhost / 127.0.0.1 / local.id.seewo.com），不放开 *
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && IsLocalOrigin(origin))
            {
                resp.Headers.Add("Access-Control-Allow-Origin", origin);
                resp.Headers.Add("Vary", "Origin");
            }
            resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
            resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 200;
                resp.Close();
                return;
            }

            try
            {
                if (req.HttpMethod == "GET" && path.Equals("/getData/SSOLOGIN", StringComparison.OrdinalIgnoreCase))
                {
                    LastSsoRequestAtUtc = DateTime.UtcNow;
                    var config = _getConfig();
                    var sourceAccounts = (_getVisibleAccounts?.Invoke() ?? Array.Empty<SeewoAccount>())
                        .Where(a => !string.IsNullOrEmpty(a.Username))
                        .ToList();

                    // 始终只返回生效区（前 MaxVisibleAccounts 个）账号
                    List<SeewoAccount> visibleAccounts = sourceAccounts.Take(PluginConfig.MaxVisibleAccounts).ToList();
                    var routeIndex = 0;
                    if (config.UserListRotationEnabled && sourceAccounts.Count > PluginConfig.MaxVisibleAccounts && _userListRotation != null)
                    {
                        var rotated = _userListRotation.SelectAccountsForRequest(sourceAccounts, out routeIndex, config.UserListRotationGroupSize).Take(PluginConfig.MaxVisibleAccounts).ToList();
                        if (rotated.Count > 0) visibleAccounts = rotated;
                    }

                    var accounts = visibleAccounts
                        .Select(a => new Dictionary<string, string>
                        {
                            { "pt_nickname", a.DisplayName ?? a.UserInfo?.NickName ?? a.Username },
                            { "pt_appid", a.Id },
                            { "pt_userid", a.Id },
                            { "pt_username", a.UserInfo?.RealName ?? a.DisplayName ?? a.Username },
                            { "pt_photourl", a.UserInfo?.PhotoUrl ?? "" }
                        })
                        .ToList();

                    // 频率追踪：本次展示的账号计数+1
                    foreach (var a in visibleAccounts)
                    {
                        a.RequestCount++;
                        a.LastRequestAtUtc = DateTime.UtcNow;
                    }

                    // 通知外部本次展示了哪些账号
                    AccountsServed?.Invoke(visibleAccounts.Select(a => a.Id).ToList());
                    ThrottledSaveConfig();

                    await WriteJson(resp, new { message = "success", statusCode = "200", data = accounts });
                    Log(config.UserListRotationEnabled
                        ? $"SSOLOGIN: 返回第 {routeIndex + 1} 组 {accounts.Count} 个账号"
                        : $"SSOLOGIN: 返回 {accounts.Count} 个账号");
                    return;
                }

                if (req.HttpMethod == "GET" && path.StartsWith("/getData/SSOLOGIN/", StringComparison.OrdinalIgnoreCase))
                {
                    LastSsoRequestAtUtc = DateTime.UtcNow;
                    var userId = path.Substring("/getData/SSOLOGIN/".Length);
                    Log($"SSOLOGIN/{{userid}}: 收到请求 userId={userId}");
                    var config = _getConfig();
                    var account = config.Accounts.FirstOrDefault(a => a.Id == userId || a.Username == userId);

                    // 频率追踪
                    if (account != null)
                    {
                        account.RequestCount++;
                        account.LastRequestAtUtc = DateTime.UtcNow;
                        ThrottledSaveConfig();
                    }

                    if (account == null)
                    {
                        resp.StatusCode = 404;
                        await WriteJson(resp, new { message = "user_not_found", statusCode = "404" });
                        return;
                    }

                    SeewoLoginResult loginResult;
                    if (string.IsNullOrEmpty(account.Password))
                    {
                        if (!_authService.IsSessionFor(account))
                            _tryRestoreQrSession?.Invoke(account);

                        if (_authService.IsSessionFor(account))
                        {
                            loginResult = await _authService.ExchangeCurrentTokenAsync();
                            if (!loginResult.Success)
                            {
                                resp.StatusCode = 401;
                                await WriteJson(resp, new { message = "qr_token_invalid", statusCode = "401", detail = loginResult.ErrorMessage });
                                Log($"SSOLOGIN/{userId}: Token 换发失败，需要重新扫码; reason={loginResult.ErrorMessage}");
                                return;
                            }
                            _onQrTokenValidated?.Invoke(account, loginResult.Token);
                            Log($"SSOLOGIN/{userId}: Token 换发成功，已更新持久化 Token");
                        }
                        else
                        {
                            loginResult = _authService.GetCurrentLoginResult();
                        }
                    }
                    else
                    {
                        loginResult = await _authService.LoginAsync(account.Username, account.DecryptedPassword);
                    }

                    if (!loginResult.Success)
                    {
                        resp.StatusCode = 401;
                        await WriteJson(resp, new { message = "login_failed", statusCode = "401", detail = loginResult.ErrorMessage });
                        Log($"SSOLOGIN/{userId}: 登录失败 - {loginResult.ErrorMessage}");
                        return;
                    }

                    resp.Headers.Set("Set-Cookie", $"pt_token={loginResult.Token}; Path=/; HttpOnly; SameSite=Lax");
                    resp.Headers.Set("X-Auth-Token", loginResult.Token);

                    await WriteJson(resp, new { message = "success", statusCode = "200", data = new { pt_token = loginResult.Token } });

                    account.UserInfo = loginResult.UserInfo;
                    _onLoginSuccess?.Invoke(account);

                    Log($"SSOLOGIN/{userId}: 登录成功 - {loginResult.UserInfo?.NickName}");
                    return;
                }

                if (req.HttpMethod == "GET" && path.Equals("/getData/SSOLOGOUT", StringComparison.OrdinalIgnoreCase))
                {
                    _authService.Logout();
                    await WriteJson(resp, new { message = "success", statusCode = "200" });
                    return;
                }

                if (req.HttpMethod == "POST" && (path.Equals("/savedata", StringComparison.OrdinalIgnoreCase) ||
                                                  path.Equals("/saveData", StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteJson(resp, new { message = "success", statusCode = "200" });
                    return;
                }

                resp.StatusCode = 404;
                await WriteJson(resp, new { message = "not_found", statusCode = "404" });
            }
            catch (Exception ex)
            {
                try
                {
                    resp.StatusCode = 500;
                    await WriteJson(resp, new { message = "internal_error", statusCode = "500", detail = ex.Message });
                }
                catch { }
            }
        }

        private static async Task WriteJson(HttpListenerResponse resp, object data)
        {
            var json = JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            resp.Close();
        }

        public void Dispose() => Stop();

        public event Action<string> LogMessage;
        /// <summary>当向希沃返回账号列表时触发，参数为本次返回的账号 ID 列表</summary>
        public event Action<List<string>> AccountsServed;
        private void Log(string msg) => LogMessage?.Invoke(msg);
    }
}
