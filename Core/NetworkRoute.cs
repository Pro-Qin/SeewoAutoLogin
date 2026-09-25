using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin
{
    /// <summary>
    /// 一次文本请求的结果。区分「传输层失败」（根本没拿到 HTTP 响应）和「服务端返回错误状态」。
    /// </summary>
    public sealed class HttpTextResponse
    {
        public bool TransportOk { get; set; }
        public int StatusCode { get; set; }
        public string Body { get; set; } = "";
        public string Error { get; set; } = "";

        public bool IsSuccess => TransportOk && StatusCode >= 200 && StatusCode < 300;
    }

    /// <summary>
    /// 统一的网络出口策略。
    ///
    /// 背景：用户关闭本地代理软件（Clash / v2ray / 加速器）后，Windows 里往往还留着指向
    /// 127.0.0.1:10809 之类端口的系统代理设置。.NET 的 HttpClient 默认会走这个代理，
    /// 于是所有请求都变成「由于目标计算机积极拒绝，无法连接。 (127.0.0.1:10809)」：
    ///   • 添加密码账号时提示登录失败；
    ///   • SSO 网关拿不到登录令牌，希沃白板侧表现为「登录信息过期」。
    ///
    /// 这里的处理：
    ///   1. 本地回环代理端口先做一次快速探测，端口没人监听就直接走直连（不浪费一次失败请求）；
    ///   2. 仍按系统设置走可用代理（用户确实需要代理上网时不受影响）；
    ///   3. 走代理的请求如果在传输层失败，自动用直连重试一次，并在本进程内记住该选择。
    /// </summary>
    public static class NetworkRoute
    {
        private const int LoopbackProbeTimeoutMs = 400;
        private const string DefaultProbeUrl = "https://edu.seewo.com/";

        private static readonly object Gate = new object();
        private static bool _preferDirect;
        /// <summary>直连优先的截止时间；过期后重新给代理机会，避免进程内永久粘在直连上。</summary>
        private static DateTime _preferDirectUntilUtc = DateTime.MinValue;
        private static string _lastDecision = "";

        /// <summary>诊断日志出口（由 App 挂到应用日志）。</summary>
        public static event Action<string> DiagnosticMessage;

        /// <summary>最近一次出口决策描述，便于诊断界面/日志展示。</summary>
        public static string LastDecision
        {
            get { lock (Gate) { return _lastDecision; } }
        }

        public static void Log(string message) => DiagnosticMessage?.Invoke(message);

        /// <summary>
        /// 为长期复用的 HttpClientHandler 配置代理（二维码客户端等）。
        /// </summary>
        public static void ConfigureHandler(HttpClientHandler handler, Uri target)
        {
            if (handler == null) return;

            try
            {
                if (TryResolveProxy(target, out var proxy, out var proxyUri, out var proxyAlive))
                {
                    if (proxyAlive)
                    {
                        handler.UseProxy = true;
                        // 直接用系统代理对象，保留 ProxyOverride / NO_PROXY 等绕过规则。
                        handler.Proxy = proxy ?? new WebProxy(proxyUri) { BypassProxyOnLocal = true };
                        NoteDecision($"[网络] 使用系统代理 {FormatProxy(proxyUri)}");
                        return;
                    }

                    NoteDecision($"[网络] 系统代理 {FormatProxy(proxyUri)} 未在运行，已自动改为直连");
                }

                handler.UseProxy = false;
                handler.Proxy = null;
            }
            catch (Exception ex)
            {
                Log($"[网络] 代理配置失败，改为直连: {ex.GetType().Name}");
                try
                {
                    handler.UseProxy = false;
                    handler.Proxy = null;
                }
                catch { }
            }
        }

        /// <summary>
        /// 发送请求并读取文本响应；代理不可用时自动回退直连。
        /// requestFactory 每次调用都要返回一个全新的 HttpRequestMessage（重试时不能复用）。
        /// </summary>
        public static async Task<HttpTextResponse> SendTextAsync(
            Func<HttpRequestMessage> requestFactory,
            TimeSpan timeout,
            string tag,
            CancellationToken cancellationToken = default)
        {
            if (requestFactory == null) throw new ArgumentNullException(nameof(requestFactory));

            var target = TryGetRequestUri(requestFactory);
            var attempts = BuildAttempts(target, tag);

            string firstError = null;
            foreach (var attempt in attempts)
            {
                try
                {
                    using var handler = CreateHandler(attempt);
                    using var client = new HttpClient(handler) { Timeout = timeout };
                    using var request = requestFactory();
                    using var response = await client
                        .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                        .ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (attempt.UseProxy)
                    {
                        // 代理重新可用：清除直连优先，下次起恢复按系统代理优先。
                        lock (Gate)
                        {
                            _preferDirect = false;
                            _preferDirectUntilUtc = DateTime.MinValue;
                        }
                        NoteDecision($"[网络] {tag} 通过{attempt.Label}成功");
                    }
                    else if (_preferDirect)
                    {
                        NoteDecision($"[网络] {tag} 已改用{attempt.Label}");
                    }

                    return new HttpTextResponse
                    {
                        TransportOk = true,
                        StatusCode = (int)response.StatusCode,
                        Body = body
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    var detail = $"{attempt.Label}不可用（{Describe(ex)}）";
                    if (firstError == null) firstError = detail;
                    Log($"[网络] {tag} 通过{attempt.Label}失败: {Describe(ex)}");

                    if (attempt.UseProxy)
                    {
                        // 代理端口能连上但请求仍失败（代理未启动完成 / 规则异常 / 被服务端拒绝），
                        // 之后优先直连，同时保留代理作为后续回退项；10 分钟后重新给代理机会。
                        lock (Gate)
                        {
                            _preferDirect = true;
                            _preferDirectUntilUtc = DateTime.UtcNow.AddMinutes(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (firstError == null) firstError = $"{attempt.Label}异常（{ex.Message}）";
                    Log($"[网络] {tag} 通过{attempt.Label}异常: {ex.GetType().Name}");
                    break;
                }
            }

            return new HttpTextResponse
            {
                TransportOk = false,
                Error = firstError ?? "网络请求失败"
            };
        }

        /// <summary>把传输层失败包装成给用户看的提示。</summary>
        public static string DescribeFailure(HttpTextResponse response)
        {
            var detail = response == null || string.IsNullOrWhiteSpace(response.Error)
                ? "网络不可用"
                : response.Error;
            return $"网络连接失败：{detail}";
        }

        private static List<RouteAttempt> BuildAttempts(Uri target, string tag)
        {
            var attempts = new List<RouteAttempt>();

            if (TryResolveProxy(target, out var proxy, out var proxyUri, out var proxyAlive))
            {
                if (proxyAlive)
                {
                    var proxyAttempt = new RouteAttempt(true, proxy, proxyUri, $"系统代理 {FormatProxy(proxyUri)}");
                    var directAttempt = new RouteAttempt(false, null, null, "直连");
                    bool preferDirect;
                    lock (Gate)
                    {
                        preferDirect = _preferDirect && DateTime.UtcNow < _preferDirectUntilUtc;
                        if (!preferDirect) _preferDirect = false;
                    }
                    if (preferDirect)
                    {
                        attempts.Add(directAttempt);
                        attempts.Add(proxyAttempt);
                    }
                    else
                    {
                        attempts.Add(proxyAttempt);
                        attempts.Add(directAttempt);
                    }
                    return attempts;
                }

                NoteDecision($"[网络] 系统代理 {FormatProxy(proxyUri)} 未在运行，已自动改为直连");
            }

            attempts.Add(new RouteAttempt(false, null, null, "直连"));
            return attempts;
        }

        private static HttpClientHandler CreateHandler(RouteAttempt attempt)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };

            if (attempt.UseProxy)
            {
                handler.UseProxy = true;
                // 优先使用系统代理对象以保留 ProxyOverride / NO_PROXY 绕过规则。
                handler.Proxy = attempt.Proxy ?? (attempt.ProxyUri == null
                    ? null
                    : new WebProxy(attempt.ProxyUri) { BypassProxyOnLocal = true });
            }
            else
            {
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            return handler;
        }

        private static Uri TryGetRequestUri(Func<HttpRequestMessage> requestFactory)
        {
            try
            {
                using var probe = requestFactory();
                return probe?.RequestUri;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析当前系统代理。返回 false 表示「没有配置代理」，此时应直连。
        /// proxy 为系统代理对象（保留绕过规则），proxyUri 为解析出的代理地址，
        /// proxyAlive 仅对本地回环代理做真实端口探测，非本机代理一律视为可用。
        /// </summary>
        private static bool TryResolveProxy(Uri target, out IWebProxy proxy, out Uri proxyUri, out bool proxyAlive)
        {
            proxy = null;
            proxyUri = null;
            proxyAlive = false;

            if (target == null)
            {
                try { target = new Uri(DefaultProbeUrl); } catch { return false; }
            }

            try
            {
                var defaultProxy = WebRequest.DefaultWebProxy;
                if (defaultProxy == null) return false;

                var candidate = defaultProxy.GetProxy(target);
                if (candidate == null) return false;

                // GetProxy 在未配置代理时会原样返回目标地址，这里据此判断“其实没有代理”。
                if (IsSameEndpoint(candidate, target)) return false;

                proxy = defaultProxy;
                proxyUri = candidate;
                proxyAlive = IsProxyEndpointAlive(candidate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProxyEndpointAlive(Uri proxyUri)
        {
            var host = proxyUri.Host;
            if (string.IsNullOrWhiteSpace(host)) return false;

            // 只对本地代理做端口探测：远程代理探测成本高，而且真实请求失败后仍会回退直连。
            if (!IsLoopbackHost(host)) return true;

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, proxyUri.Port);
                if (!connectTask.Wait(LoopbackProbeTimeoutMs)) return false;
                return connectTask.IsCompletedSuccessfully && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)) return true;
            return host.StartsWith("127.", StringComparison.Ordinal);
        }

        private static bool IsSameEndpoint(Uri candidate, Uri target)
        {
            if (candidate == null || target == null) return false;
            return string.Equals(candidate.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
                   candidate.Port == target.Port;
        }

        private static bool IsTransportFailure(Exception ex)
        {
            return ex is HttpRequestException
                || ex is SocketException
                || ex is System.IO.IOException
                || ex is System.Security.Authentication.AuthenticationException
                || ex is TaskCanceledException
                || ex is OperationCanceledException;
        }

        private static string Describe(Exception ex)
        {
            var messages = new List<string>();
            for (var current = ex; current != null && messages.Count < 3; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                    messages.Add(current.Message.Trim());
            }
            return messages.Count == 0 ? ex.GetType().Name : string.Join(" | ", messages);
        }

        private static string FormatProxy(Uri proxyUri)
            => proxyUri == null ? "<none>" : $"{proxyUri.Host}:{proxyUri.Port}";

        private static void NoteDecision(string message)
        {
            lock (Gate)
            {
                if (string.Equals(_lastDecision, message, StringComparison.Ordinal)) return;
                _lastDecision = message;
            }
            Log(message);
        }

        private readonly struct RouteAttempt
        {
            public RouteAttempt(bool useProxy, IWebProxy proxy, Uri proxyUri, string label)
            {
                UseProxy = useProxy;
                Proxy = proxy;
                ProxyUri = proxyUri;
                Label = label;
            }

            public bool UseProxy { get; }
            public IWebProxy Proxy { get; }
            public Uri ProxyUri { get; }
            public string Label { get; }
        }
    }
}
