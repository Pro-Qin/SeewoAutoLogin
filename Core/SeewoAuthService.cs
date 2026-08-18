using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin
{
    public class SeewoAuthService : IDisposable
    {
        private const string SEEWO_EDU_BASE = "https://edu.seewo.com";
        private const string LOGIN_URL = SEEWO_EDU_BASE + "/api/v1/auth/login";
        private const string USER_INFO_URL = SEEWO_EDU_BASE + "/api/v2/user/info";
        private const string TOKEN_EXCHANGE_URL = "https://account.seewo.com/seewo-account/api/v1/auth/";
        private const string AUTH_APP = "EasiNoteAndroid";
        private const string AUTH_REFER = "EnAppAndroid";
        private const string USER_AGENT = "okhttp/3.12.12";

        private readonly HttpClient _http;
        private string _token;
        private SeewoUserInfo _userInfo;

        public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_token);
        public string Token => _token;
        public SeewoUserInfo UserInfo => _userInfo;

        public SeewoAuthService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
        }

        public async Task<SeewoLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            try
            {
                var md5Pwd = ComputeMd5(password);
                var traceId = Guid.NewGuid().ToString("N")[..32];

                var payload = new
                {
                    username = username,
                    password = md5Pwd,
                    captcha = (string)null,
                    phoneCountryCode = ""
                };

                var json = JsonSerializer.Serialize(payload);
                var request = new HttpRequestMessage(HttpMethod.Post, LOGIN_URL)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-APM-TraceId", traceId);
                request.Headers.Add("Cookie", "x-auth-app=EasiNote5; x-auth-token=");

                var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return new SeewoLoginResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {(int)response.StatusCode}"
                    };
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
                {
                    var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "未知错误";
                    return new SeewoLoginResult { Success = false, ErrorMessage = msg };
                }

                var token = root.GetProperty("data").GetProperty("token").GetString();
                if (string.IsNullOrEmpty(token))
                {
                    return new SeewoLoginResult { Success = false, ErrorMessage = "未获取到 token" };
                }

                _token = token;
                await FetchUserInfoAsync(cancellationToken);

                return new SeewoLoginResult
                {
                    Success = true,
                    Token = token,
                    UserInfo = _userInfo
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SeewoLoginResult { Success = false, ErrorMessage = "已取消" };
            }
            catch (TaskCanceledException)
            {
                return new SeewoLoginResult { Success = false, ErrorMessage = "请求超时" };
            }
            catch (Exception ex)
            {
                return new SeewoLoginResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<SeewoUserInfo> FetchUserInfoAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_token)) return null;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, USER_INFO_URL);
                request.Headers.Add("X-auth-refer", "EnAppAndroid");
                request.Headers.Add("X-Crypto-Version", "1");
                request.Headers.Add("Cookie", $"x-auth-app=EasiNoteAndroid; x-auth-token={_token}");

                var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
                    return null;

                var data = root.GetProperty("data");
                _userInfo = new SeewoUserInfo
                {
                    NickName = GetJsonString(data, "nickName"),
                    RealName = GetJsonString(data, "realName"),
                    UserName = GetJsonString(data, "username"),
                    Phone = GetJsonString(data, "phone"),
                    PhotoUrl = GetJsonString(data, "photoUrl"),
                    UnitName = GetJsonString(data, "unitName"),
                    StageName = GetJsonString(data, "stageName"),
                    SubjectName = GetJsonString(data, "subjectName"),
                    AccountId = GetJsonString(data, "accountId"),
                    Uid = GetJsonString(data, "uid")
                };
                return _userInfo;
            }
            catch
            {
                return null;
            }
        }

        public void AcceptQrLogin(QrLoginOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.Token))
                throw new ArgumentException("扫码登录结果无效。", nameof(outcome));

            _token = outcome.Token;
            _userInfo = outcome.UserInfo;
        }

        public void RestoreQrSession(string token, SeewoUserInfo userInfo)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("扫码会话令牌无效。", nameof(token));

            _token = token;
            _userInfo = userInfo;
        }

        public bool IsSessionFor(SeewoAccount account)
        {
            if (account?.UserInfo == null || _userInfo == null || !IsLoggedIn) return false;
            if (!string.IsNullOrWhiteSpace(account.UserInfo.AccountId) &&
                string.Equals(_userInfo.AccountId, account.UserInfo.AccountId, StringComparison.Ordinal)) return true;
            return !string.IsNullOrWhiteSpace(account.UserInfo.UserName) &&
                   string.Equals(_userInfo.UserName, account.UserInfo.UserName, StringComparison.Ordinal);
        }

        public event Action<string> DiagnosticMessage;

        public async Task<SeewoLoginResult> ValidateCurrentTokenAsync(CancellationToken cancellationToken = default)
        {
            var oldToken = _token;
            DiagnosticMessage?.Invoke($"[TokenCheck] start; old-token-present={!string.IsNullOrWhiteSpace(oldToken)}");
            if (string.IsNullOrWhiteSpace(_token))
            {
                DiagnosticMessage?.Invoke("[TokenCheck] skipped; reason=no-token");
                return new SeewoLoginResult { Success = false, ErrorMessage = "没有可用的登录令牌" };
            }

            try
            {
                var outcome = await ValidateTokenWithCheckTokenAsync(_token, cancellationToken).ConfigureAwait(false);
                if (outcome == null || string.IsNullOrWhiteSpace(outcome.Token))
                {
                    DiagnosticMessage?.Invoke("[TokenCheck] result=invalid; new-token-present=false");
                    return new SeewoLoginResult { Success = false, ErrorMessage = "Token 已失效" };
                }

                var changed = !string.Equals(oldToken, outcome.Token, StringComparison.Ordinal);
                _token = outcome.Token;
                _userInfo = outcome.UserInfo ?? _userInfo;
                DiagnosticMessage?.Invoke($"[TokenCheck] result=valid; new-token-present=true; token-changed={changed}; user-info-present={_userInfo != null}");
                return new SeewoLoginResult { Success = true, Token = _token, UserInfo = _userInfo };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DiagnosticMessage?.Invoke("[TokenCheck] result=cancelled");
                return new SeewoLoginResult { Success = false, ErrorMessage = "已取消" };
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested == false)
            {
                DiagnosticMessage?.Invoke("[TokenCheck] result=timeout");
                return new SeewoLoginResult { Success = false, ErrorMessage = "Token 校验超时" };
            }
            catch (Exception ex)
            {
                DiagnosticMessage?.Invoke($"[TokenCheck] result=error; type={ex.GetType().Name}");
                return new SeewoLoginResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<SeewoLoginResult> ExchangeCurrentTokenAsync(CancellationToken cancellationToken = default)
        {
            var oldToken = _token;
            DiagnosticMessage?.Invoke($"[TokenExchange] start; old-token-present={!string.IsNullOrWhiteSpace(oldToken)}");
            if (string.IsNullOrWhiteSpace(oldToken))
                return new SeewoLoginResult { Success = false, ErrorMessage = "没有可用的登录令牌" };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, TOKEN_EXCHANGE_URL + Uri.EscapeDataString(oldToken) + "/exchange");
                request.Headers.TryAddWithoutValidation("x-auth-app", "EasiNote5");
                request.Headers.TryAddWithoutValidation("Cookie", "x-auth-app=EasiNote5");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                DiagnosticMessage?.Invoke($"[TokenExchange] response; status={(int)response.StatusCode}; content-type={response.Content.Headers.ContentType?.MediaType ?? "<missing>"}; body={SanitizeJsonForLog(body)}");
                if (!response.IsSuccessStatusCode)
                {
                    DiagnosticMessage?.Invoke($"[TokenExchange] result=http-error; status={(int)response.StatusCode}");
                    return new SeewoLoginResult { Success = false, ErrorMessage = $"HTTP {(int)response.StatusCode}" };
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
                    ? dataElement : root;
                var newToken = GetJsonString(data, "token");
                if (string.IsNullOrWhiteSpace(newToken))
                {
                    var message = GetJsonString(root, "message") ?? GetJsonString(root, "msg") ?? "Token 换发失败";
                    DiagnosticMessage?.Invoke("[TokenExchange] result=invalid; new-token-present=false");
                    return new SeewoLoginResult { Success = false, ErrorMessage = message };
                }

                _token = newToken;
                var changed = !string.Equals(oldToken, newToken, StringComparison.Ordinal);
                await FetchUserInfoAsync(cancellationToken).ConfigureAwait(false);
                DiagnosticMessage?.Invoke($"[TokenExchange] result=success; new-token-present=true; token-changed={changed}; user-info-present={_userInfo != null}");
                return new SeewoLoginResult { Success = true, Token = _token, UserInfo = _userInfo };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DiagnosticMessage?.Invoke("[TokenExchange] result=cancelled");
                return new SeewoLoginResult { Success = false, ErrorMessage = "已取消" };
            }
            catch (TaskCanceledException)
            {
                DiagnosticMessage?.Invoke("[TokenExchange] result=timeout");
                return new SeewoLoginResult { Success = false, ErrorMessage = "Token 换发超时" };
            }
            catch (Exception ex)
            {
                DiagnosticMessage?.Invoke($"[TokenExchange] result=error; type={ex.GetType().Name}");
                return new SeewoLoginResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<QrLoginOutcome> ValidateTokenWithCheckTokenAsync(string token, CancellationToken cancellationToken)
        {
            using var client = new SeewoQrLoginClient();
            return await client.CheckTokenAsync(token, cancellationToken).ConfigureAwait(false);
        }

        public SeewoLoginResult GetCurrentLoginResult()
        {
            return IsLoggedIn
                ? new SeewoLoginResult { Success = true, Token = _token, UserInfo = _userInfo }
                : new SeewoLoginResult { Success = false, ErrorMessage = "没有可用的登录令牌" };
        }

        public void Logout()
        {
            _token = null;
            _userInfo = null;
        }

        private static string SanitizeJsonForLog(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "<empty>";
            try
            {
                using var document = JsonDocument.Parse(body);
                return SanitizeJsonElement(document.RootElement);
            }
            catch
            {
                return $"<non-json,length={body.Length}>";
            }
        }

        private static string SanitizeJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(property =>
                    $"\"{property.Name}\":{(IsSensitiveJsonField(property.Name) ? "\"<redacted>\"" : SanitizeJsonElement(property.Value))}")) + "}",
                JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(SanitizeJsonElement)) + "]",
                JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => "null"
            };
        }

        private static bool IsSensitiveJsonField(string name)
        {
            return name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("phone", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetJsonString(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() : "";
        }

        private static string ComputeMd5(string input)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }

    public class SeewoLoginResult
    {
        public bool Success { get; set; }
        public string Token { get; set; }
        public string ErrorMessage { get; set; }
        public SeewoUserInfo UserInfo { get; set; }
    }

    public class SeewoUserInfo
    {
        public string NickName { get; set; }
        public string RealName { get; set; }
        public string UserName { get; set; }
        public string Phone { get; set; }
        public string PhotoUrl { get; set; }
        public string UnitName { get; set; }
        public string StageName { get; set; }
        public string SubjectName { get; set; }
        public string AccountId { get; set; }
        public string Uid { get; set; }
    }
}
