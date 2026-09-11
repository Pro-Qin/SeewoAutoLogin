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

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        private string _token;
        private SeewoUserInfo _userInfo;

        public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_token);
        public string Token => _token;
        public SeewoUserInfo UserInfo => _userInfo;

        public async Task<SeewoLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await SendLoginRequestAsync(username, password, cancellationToken).ConfigureAwait(false);
                DiagnosticMessage?.Invoke(
                    $"[Login] response; transport={(response.TransportOk ? "ok" : "failed")}; " +
                    $"status={(response.TransportOk ? response.StatusCode.ToString() : "-")}; body={SanitizeJsonForLog(response.Body)}");

                if (!response.TransportOk)
                    return new SeewoLoginResult { Success = false, ErrorMessage = NetworkRoute.DescribeFailure(response) };

                if (!response.IsSuccess)
                {
                    var httpMessage = ReadApiMessage(response.Body);
                    return new SeewoLoginResult
                    {
                        Success = false,
                        ErrorMessage = string.IsNullOrWhiteSpace(httpMessage) ? $"HTTP {response.StatusCode}" : httpMessage
                    };
                }

                var result = ParseLoginBody(response.Body);
                if (!result.Success) return result;

                _token = result.Token;
                await FetchUserInfoAsync(cancellationToken).ConfigureAwait(false);
                result.UserInfo = _userInfo;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SeewoLoginResult { Success = false, ErrorMessage = "已取消" };
            }
            catch (Exception ex)
            {
                return new SeewoLoginResult { Success = false, ErrorMessage = FriendlyMessage(ex) };
            }
        }

        /// <summary>发送密码登录请求（不修改实例状态，可被并发调用）。</summary>
        private static Task<HttpTextResponse> SendLoginRequestAsync(string username, string password, CancellationToken cancellationToken)
        {
            var md5Pwd = ComputeMd5(password);
            var traceId = Guid.NewGuid().ToString("N")[..32];

            var json = JsonSerializer.Serialize(new
            {
                username = username,
                password = md5Pwd,
                captcha = (string)null,
                phoneCountryCode = ""
            });

            return NetworkRoute.SendTextAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, LOGIN_URL)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
                request.Headers.TryAddWithoutValidation("X-APM-TraceId", traceId);
                request.Headers.TryAddWithoutValidation("Cookie", "x-auth-app=EasiNote5; x-auth-token=");
                return request;
            }, RequestTimeout, "密码登录", cancellationToken);
        }

        /// <summary>
        /// 解析登录响应。希沃登录失败时返回的是 {"error_code":4908,"message":"账户不存在"}，
        /// 既没有 code 也没有 data；旧实现直接 GetProperty("data") 会抛 KeyNotFoundException，
        /// 界面上只能看到 “The given key was not present in the dictionary.”。
        /// </summary>
        private static SeewoLoginResult ParseLoginBody(string body)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(body);
                root = document.RootElement.Clone();
            }
            catch
            {
                return new SeewoLoginResult { Success = false, ErrorMessage = "登录接口返回了无法解析的数据" };
            }

            var errorCode = GetJsonInt(root, "error_code") ?? GetJsonInt(root, "code");
            if (errorCode.HasValue && errorCode.Value != 0)
            {
                return new SeewoLoginResult
                {
                    Success = false,
                    ErrorMessage = ReadApiMessage(root) ?? $"登录失败（错误码 {errorCode.Value}）"
                };
            }

            if (TryGetJsonObject(root, "data", out var data))
            {
                var token = GetJsonString(data, "token");
                if (!string.IsNullOrWhiteSpace(token))
                    return new SeewoLoginResult { Success = true, Token = token };
            }

            var rootToken = GetJsonString(root, "token");
            if (!string.IsNullOrWhiteSpace(rootToken))
                return new SeewoLoginResult { Success = true, Token = rootToken };

            return new SeewoLoginResult
            {
                Success = false,
                ErrorMessage = ReadApiMessage(root) ?? "登录接口未返回登录令牌，请检查账号与密码"
            };
        }

        private static string ReadApiMessage(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return ReadApiMessage(document.RootElement);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadApiMessage(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            var message = GetJsonString(root, "message");
            if (string.IsNullOrWhiteSpace(message)) message = GetJsonString(root, "msg");
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }

        private static string FriendlyMessage(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException) return "请求超时";
            if (ex is HttpRequestException) return $"网络请求失败：{ex.Message}";
            return ex.Message;
        }

        public async Task<SeewoUserInfo> FetchUserInfoAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_token)) return null;

            try
            {
                var token = _token;
                var response = await NetworkRoute.SendTextAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, USER_INFO_URL);
                    request.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
                    request.Headers.TryAddWithoutValidation("X-auth-refer", AUTH_REFER);
                    request.Headers.TryAddWithoutValidation("X-Crypto-Version", "1");
                    request.Headers.TryAddWithoutValidation("Cookie", $"x-auth-app={AUTH_APP}; x-auth-token={token}");
                    return request;
                }, RequestTimeout, "用户信息", cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess) return null;

                using var doc = JsonDocument.Parse(response.Body);
                var root = doc.RootElement;

                var errorCode = GetJsonInt(root, "error_code") ?? GetJsonInt(root, "code");
                if (errorCode.HasValue && errorCode.Value != 0)
                    return null;

                if (!TryGetJsonObject(root, "data", out var data))
                    return null;

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
                var response = await NetworkRoute.SendTextAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, TOKEN_EXCHANGE_URL + Uri.EscapeDataString(oldToken) + "/exchange");
                    request.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
                    request.Headers.TryAddWithoutValidation("x-auth-app", "EasiNote5");
                    request.Headers.TryAddWithoutValidation("Cookie", "x-auth-app=EasiNote5");
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    return request;
                }, RequestTimeout, "Token 换发", cancellationToken).ConfigureAwait(false);

                var body = response.Body ?? "";
                DiagnosticMessage?.Invoke(
                    $"[TokenExchange] response; transport={(response.TransportOk ? "ok" : "failed")}; " +
                    $"status={(response.TransportOk ? response.StatusCode.ToString() : "-")}; body={SanitizeJsonForLog(body)}");

                if (!response.TransportOk)
                {
                    DiagnosticMessage?.Invoke("[TokenExchange] result=transport-error");
                    return new SeewoLoginResult { Success = false, ErrorMessage = NetworkRoute.DescribeFailure(response) };
                }

                if (!response.IsSuccess)
                {
                    DiagnosticMessage?.Invoke($"[TokenExchange] result=http-error; status={response.StatusCode}");
                    return new SeewoLoginResult { Success = false, ErrorMessage = $"HTTP {response.StatusCode}" };
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var data = TryGetJsonObject(root, "data", out var dataElement) ? dataElement : root;
                var newToken = GetJsonString(data, "token");
                if (string.IsNullOrWhiteSpace(newToken))
                {
                    var message = ReadApiMessage(root) ?? "Token 换发失败";
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
            return TryGetJsonProperty(el, name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() : "";
        }

        private static int? GetJsonInt(JsonElement element, string name)
        {
            if (!TryGetJsonProperty(element, name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
            return null;
        }

        private static bool TryGetJsonObject(JsonElement element, string name, out JsonElement value)
        {
            if (TryGetJsonProperty(element, name, out value) && value.ValueKind == JsonValueKind.Object) return true;
            value = default;
            return false;
        }

        /// <summary>大小写不敏感且不会在非对象上抛异常的属性读取。</summary>
        private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            return false;
        }

        private static string ComputeMd5(string input)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose()
        {
            // 每个请求使用独立的 HttpClient（见 NetworkRoute），无需长期持有的连接池。
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
