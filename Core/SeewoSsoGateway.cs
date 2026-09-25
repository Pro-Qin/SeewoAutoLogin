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
        private readonly SemaphoreSlim _loginGate = new SemaphoreSlim(1, 1);
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

        /// <summary>端口被占用且占用者疑似希沃 EasiAgent 时，由上层弹窗征求用户同意（返回 true 才结束该进程）</summary>
        public Func<int, string, bool> ConfirmStopEasiAgent { get; set; }

        /// <summary>实际监听端口发生变化时触发（用于记录与界面告警）</summary>
        public event Action<int> PortChanged;

        /// <summary>
        /// 希沃固定请求的端口。网关必须占用它，否则希沃永远拿不到账号列表、
        /// 也就不会出现快捷登录入口 —— 换用备用端口对希沃没有任何意义。
        /// </summary>
        public const int SeewoExpectedPort = 24300;

        /// <summary>当前是否没有监听在希沃期望的端口上（此时快捷登录不会生效，界面需要显式告警）</summary>
        public bool IsPortMismatched => Port != SeewoExpectedPort;

        public void Start()
        {
            if (IsRunning) return;

            if (!Services.HostsFileService.EnsureLoopbackMapping(out var hostsError))
            {
                // 主机映射缺失/写入失败会导致希沃请求 local.id.seewo.com 时被解析到真实服务器，
                // 从而看不到本机 SSO 快捷登录入口。这里明确抛出，交由上层提示用户。
                throw new InvalidOperationException(
                    $"无法将 {Services.HostsFileService.HostName} 映射到 127.0.0.1（hosts 写入失败：{hostsError}）。请以管理员权限运行本程序。");
            }

            // 首选端口始终是希沃期望的端口（不使用上一次记录的备用端口，避免“越修越偏”）
            var preferredPort = SeewoExpectedPort;

            // 1) 首选端口：希沃 EasiAgent 占用时按用户确认结束它，并重试到端口真正释放
            if (TryStartAtPort(preferredPort, allowEasiAgentHandoff: true, out var error))
            {
                CompleteStartup(preferredPort);
                return;
            }

            // 权限不足等非占用类问题：如实抛出，不用备用端口掩盖
            if (error is InvalidOperationException || !IsPortListening(preferredPort)) throw error;

            // 2) EasiAgent 仍占着端口（用户拒绝结束、或反复被希沃拉起）：必须报错而不是换端口，
            //    否则程序“看起来正常”，用户却怎么都等不到快捷登录入口。
            if (TryFindTrustedEasiAgent(out var easiPid, out _))
            {
                throw new InvalidOperationException(
                    $"希沃 EasiAgent（pid={easiPid}）仍占用端口 {preferredPort}，SSO 快捷登录无法生效。" +
                    $"请允许结束 EasiAgent，或先退出希沃白板后点击「一键修复」。",
                    error);
            }

            // 3) 首选端口被其它程序占用：退到备用端口保证网关可用，但希沃仍请求原端口，界面会给出明确告警
            for (var port = preferredPort + 1; port <= preferredPort + 9; port++)
            {
                if (TryStartAtPort(port, allowEasiAgentHandoff: false, out _))
                {
                    Log($"[WARN] 首选端口 {preferredPort} 被其它程序占用，SSO 网关已改用 {port}；" +
                        $"希沃固定请求 {preferredPort}，在其被释放前不会出现快捷登录入口。");
                    CompleteStartup(port);
                    return;
                }
            }

            throw new InvalidOperationException(
                $"SSO 网关启动失败：端口 {preferredPort}~{preferredPort + 9} 均不可用。" +
                $"最后一次错误：{error?.Message ?? "未知"}", error);
        }

        private void CompleteStartup(int actualPort)
        {
            Port = actualPort;
            if (actualPort != SeewoExpectedPort)
            {
                try { PortChanged?.Invoke(actualPort); } catch { }
            }
            _ = Task.Run(() => ListenLoop(_listener, _cts.Token));
            Log($"SSO 网关已启动: http://localhost:{Port}");
        }

        /// <summary>
        /// 尝试在指定端口启动监听。allowEasiAgentHandoff 为 true 时，允许在用户确认后结束希沃 EasiAgent，
        /// 并在端口释放前反复重试（http.sys 回收注册需要时间，希沃也可能重新拉起 EasiAgent）。
        /// </summary>
        private bool TryStartAtPort(int port, bool allowEasiAgentHandoff, out Exception error)
        {
            error = null;
            var maxRounds = allowEasiAgentHandoff ? 3 : 1;
            bool? userApproved = null;

            for (var round = 1; round <= maxRounds; round++)
            {
                try
                {
                    ResetListener(port);
                    _listener.Start();
                    if (round > 1) Log($"已接管端口 {port}（第 {round} 轮重试成功）");
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex;

                    // 权限不足（需要管理员 / URL ACL）：换端口或重试都无意义
                    if (IsAccessDenied(ex))
                    {
                        Log($"[ERROR] SSO 网关无法监听端口 {port}：拒绝访问（需要管理员权限）");
                        throw new InvalidOperationException(
                            $"以当前权限无法监听本地 SSO 网关（拒绝访问，端口 {port}）。请以管理员身份重新运行本程序。", ex);
                    }

                    if (!allowEasiAgentHandoff) return false;
                    if (!IsPortListening(port)) return false; // 不是端口占用

                    Log($"SSO 网关端口 {port} 已被占用（第 {round}/{maxRounds} 轮）");

                    if (userApproved == null)
                    {
                        // 只询问一次，后续轮次沿用用户的选择，避免反复弹窗
                        if (!TryAskToStopEasiAgent(port, out var askError))
                        {
                            if (!string.IsNullOrEmpty(askError)) Log(askError);
                            return false;
                        }
                        userApproved = true;
                    }

                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < deadline && IsPortListening(port))
                    {
                        // 希沃会重新拉起 EasiAgent：等待期间持续清理，直到端口空出来
                        if (TryFindTrustedEasiAgent(out var pid, out _))
                        {
                            Log($"端口 {port} 仍被占用，结束希沃 EasiAgent; pid={pid}");
                            TryStopTrustedEasiAgent(out _);
                        }
                        Thread.Sleep(500);
                    }

                    if (!IsPortListening(port))
                    {
                        Log($"端口 {port} 已释放，重新尝试绑定");
                        continue;
                    }

                    Log($"[WARN] 端口 {port} 等待 15 秒后仍被占用");
                }
            }
            return false;
        }

        /// <summary>端口被占用时询问用户是否结束希沃 EasiAgent；未注入回调或用户拒绝时返回 false（不结束任何进程）</summary>
        private bool TryAskToStopEasiAgent(int port, out string error)
        {
            error = null;
            if (!TryFindTrustedEasiAgent(out var pid, out var path))
            {
                error = $"端口 {port} 被其它程序占用（未发现希沃 EasiAgent），将尝试使用备用端口";
                return false;
            }

            var approved = false;
            try { approved = ConfirmStopEasiAgent?.Invoke(pid, path) ?? false; }
            catch (Exception ex) { Log($"询问是否结束 EasiAgent 失败: {ex.GetType().Name}"); }

            if (!approved)
            {
                error = $"用户未同意结束希沃 EasiAgent(pid={pid})，将尝试使用备用端口";
                return false;
            }

            Log($"正在结束希沃 EasiAgent; pid={pid}");
            if (!TryStopTrustedEasiAgent(out var stopError))
            {
                error = $"结束希沃 EasiAgent 失败: {stopError}";
                return false;
            }
            return true;
        }

        /// <summary>结束受信任路径的希沃 EasiAgent（调用方必须先征得用户同意）</summary>
        private bool TryStopTrustedEasiAgent(out string error)
        {
            error = null;
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
                    process.Kill();
                    process.WaitForExit(5000);
                    if (process.HasExited) return true;
                    error = "进程未在超时内退出";
                }
                catch (Exception ex)
                {
                    error = $"{ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }

        /// <summary>查找受信任路径的希沃 EasiAgent 进程（仅用于询问用户，不代表它一定占用了本端口）</summary>
        private static bool TryFindTrustedEasiAgent(out int pid, out string path)
        {
            pid = 0;
            path = null;
            foreach (var process in Process.GetProcessesByName("EasiAgent"))
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (!IsTrustedEasiAgentPath(file)) continue;
                    pid = process.Id;
                    path = file;
                    return true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }

        private void ResetListener(int port)
        {
            try { _listener?.Close(); } catch { }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Prefixes.Add($"http://{SeeSoLocalHost}:{port}/");
            }
            catch (Exception ex)
            {
                // 该前缀是希沃识别快捷登录的关键，注册失败必须显式暴露，不能静默继续
                Log($"注册 {SeeSoLocalHost} 前缀失败: {ex.GetType().Name} - {ex.Message}");
                throw new InvalidOperationException(
                    $"无法注册 http://{SeeSoLocalHost}:{port}/ 监听前缀，SSO 快捷登录将不可用。请以管理员权限运行本程序。", ex);
            }
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

        /// <summary>判断异常是否为“拒绝访问”（HttpListener 在非管理员/缺少 URL ACL 时的典型错误）</summary>
        private static bool IsAccessDenied(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is HttpListenerException listenerEx && listenerEx.ErrorCode == 5) return true;
                var message = current.Message ?? "";
                if (message.Contains("拒绝访问") ||
                    message.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
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
            return IsAllowedGatewayHost(uri.Host);
        }

        private static bool IsAllowedGatewayHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1"
                || host == "[::1]"
                || string.Equals(host, SeeSoLocalHost, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>hosts 映射是否存在（读写语义统一由 HostsFileService 负责：备份、标记、原编码写回）</summary>
        private static bool EnsureHostsMapping() => Services.HostsFileService.HasLoopbackMapping();

        public void Stop()
        {
            var cts = _cts;
            var listener = _listener;
            _cts = null;
            _listener = null;
            try { cts?.Cancel(); } catch { }
            try { listener?.Close(); } catch { }
            try { cts?.Dispose(); } catch { }
            Log("SSO 网关已停止");
        }

        private async Task ListenLoop(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener?.IsListening == true)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (HttpListenerException ex)
                {
                    if (ct.IsCancellationRequested) break;
                    // 监听异常若直接退出会导致「界面显示运行中、实际不再响应」的静默失效
                    Log($"[ERROR] SSO 监听循环异常: {ex.ErrorCode} {ex.Message}; 1 秒后重试");
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Log($"[ERROR] SSO 监听循环未知异常: {ex.GetType().Name} - {ex.Message}; 1 秒后重试");
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";

            // 安全：网关只服务本机（希沃通过 hosts 映射访问 127.0.0.1）。
            // 由于需要注册 local.id.seewo.com 前缀，http.sys 会把端口绑定到所有网卡，
            // 因此必须显式校验来源地址，否则同一局域网内任何机器伪造 Host 头即可拉取账号列表并换取登录令牌。
            var remoteAddress = req.RemoteEndPoint?.Address;
            if (remoteAddress != null && !System.Net.IPAddress.IsLoopback(remoteAddress))
            {
                Log($"拒绝非本机来源的 SSO 请求: {remoteAddress} {req.HttpMethod} {path}");
                try
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { message = "forbidden_remote", statusCode = "403" });
                }
                catch { }
                return;
            }

            // Host 白名单：HttpListener 只注册了 localhost / 127.0.0.1 / local.id.seewo.com 三个前缀，
            // 这里再显式收紧一次，避免以后误加通配前缀时把网关暴露给任意本机进程或网页。
            if (!IsAllowedGatewayHost(req.Url?.Host))
            {
                Log($"拒绝未知 Host 的 SSO 请求: {req.Url?.Host} {req.HttpMethod} {path}");
                try
                {
                    resp.StatusCode = 404;
                    await WriteJson(resp, new { message = "not_found", statusCode = "404" });
                }
                catch { }
                return;
            }

            // CORS：只放行本机来源（localhost / 127.0.0.1 / local.id.seewo.com），不放开 *
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                if (IsLocalOrigin(origin))
                {
                    resp.Headers.Add("Access-Control-Allow-Origin", origin);
                    resp.Headers.Add("Vary", "Origin");
                }
                else if (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // 外部网页跨源请求：直接拒绝，避免被当成 CSRF 跳板触发登录或拉取账号列表。
                    Log($"拒绝外部 Origin 的 SSO 请求: {origin} {req.HttpMethod} {path}");
                    resp.StatusCode = 403;
                    try { await WriteJson(resp, new { message = "forbidden_origin", statusCode = "403" }); } catch { }
                    return;
                }
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

                    // 希沃短时间会并发发起多个 SSOLOGIN 请求，而 authService 只持有一份 token / userInfo，
                    // 并发登录会互相覆盖（A 的请求可能拿到 B 的 token），客户端就表现为「登录信息过期」。
                    // 这里串行化登录/换发过程，保证每个请求返回的 token 都属于它自己的账号。
                    await _loginGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
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
                            var password = account.DecryptedPassword;
                            if (string.IsNullOrEmpty(password))
                            {
                                resp.StatusCode = 401;
                                await WriteJson(resp, new
                                {
                                    message = "credential_invalid",
                                    statusCode = "401",
                                    detail = "本地保存的密码已无法解密（可能更换了 Windows 账户或系统凭据被重置），请在管理界面重新录入密码"
                                });
                                Log($"SSOLOGIN/{userId}: 本地密码无法解密，需要重新录入（不是密码错误）");
                                return;
                            }
                            loginResult = await _authService.LoginAsync(account.Username, password);
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
                    finally
                    {
                        _loginGate.Release();
                    }
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

        public void Dispose()
        {
            Stop();
            // 给在途请求留出退出时间，避免释放信号量时抛 ObjectDisposedException
            try { _loginGate.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _loginGate.Dispose(); } catch { }
        }

        public event Action<string> LogMessage;
        /// <summary>当向希沃返回账号列表时触发，参数为本次返回的账号 ID 列表</summary>
        public event Action<List<string>> AccountsServed;
        private void Log(string msg) => LogMessage?.Invoke(msg);
    }
}
