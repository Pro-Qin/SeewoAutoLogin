using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin
{
    public sealed class SeewoQrLoginClient : IDisposable
    {
        private static readonly Uri Authority = new Uri("https://id.seewo.com/");
        private const string AppCode = "EasiNote5";
        private const string Brand = "";
        private const int MaximumImageBytes = 2 * 1024 * 1024;
        private const int MaximumJsonBytes = 1024 * 1024;

        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly HttpClient _http;
        private string _lastPollDiagnostic = "";

        public event Action<string> LogMessage;

        public SeewoQrLoginClient()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AllowAutoRedirect = false
            };
            // 代理软件没运行时，系统里残留的代理设置会让所有请求直接被拒绝，这里自动判断并改为直连。
            NetworkRoute.ConfigureHandler(handler, Authority);
            _http = new HttpClient(handler)
            {
                BaseAddress = Authority,
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public async Task<QrLoginSession> CreateSessionAsync(CancellationToken cancellationToken)
        {
            Log("创建二维码会话: GET /scan/qrcode, app=EasiNote5");
            var requestUri = $"scan/qrcode?oriSys={Uri.EscapeDataString(AppCode)}&brand={Uri.EscapeDataString(Brand)}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            AddAppCookies(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(31));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            Log($"二维码会话响应: HTTP {(int)response.StatusCode}, content-type={SafeMediaType(contentType)}, content-length={SafeLength(response.Content.Headers.ContentLength)}");
            response.EnsureSuccessStatusCode();

            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                Log("二维码会话协议错误: 响应不是图片");
                throw new InvalidDataException("二维码服务未返回图片。");
            }

            var image = await ReadLimitedAsync(response.Content, MaximumImageBytes, timeout.Token).ConfigureAwait(false);
            Log($"二维码图片读取完成: {image.Length} bytes");
            if (image.Length < 8)
            {
                Log("二维码会话协议错误: 图片内容过短");
                throw new InvalidDataException("二维码图片无效。");
            }

            var sessionKey = FindCookie("qrkey");
            Log($"二维码会话 Cookie 检查: qrkey-present={!string.IsNullOrWhiteSpace(sessionKey)}");
            if (string.IsNullOrWhiteSpace(sessionKey))
                throw new InvalidDataException("二维码服务未返回会话标识。");

            return new QrLoginSession
            {
                ImageBytes = image,
                SessionKey = sessionKey,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(120)
            };
        }

        public Task<QrLoginStatus> PollAsync(QrLoginSession session, bool scanned, CancellationToken cancellationToken)
        {
            var path = scanned ? "scan/confirmStatus?type=long" : "scan/pcCheckQrcode?type=long";
            return SendStatusRequestAsync(path, session.SessionKey, scanned, cancellationToken);
        }

        public async Task<QrLoginOutcome> CompleteAsync(string temporaryToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(temporaryToken))
                throw new InvalidDataException("登录响应缺少临时凭据。");

            return await CheckTokenAsync(temporaryToken, cancellationToken).ConfigureAwait(false);
        }

        public Task<QrLoginOutcome> CheckTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("Token 不能为空。");
            return CheckTokenInternalAsync(token, cancellationToken);
        }

        private async Task<QrLoginOutcome> CheckTokenInternalAsync(string token, CancellationToken cancellationToken)
        {
            Log("校验 Token 有效性: POST /auth/checkToken, token-present=true");
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/checkToken")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json")
            };
            AddAppCookies(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            Log($"账号校验响应: HTTP {(int)response.StatusCode}, content-type={SafeMediaType(response.Content.Headers.ContentType?.MediaType)}");
            response.EnsureSuccessStatusCode();
            using var document = await ReadJsonAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            var payload = GetResponsePayload(root);
            var statusCode = GetInt(payload, "statusCode");
            Log($"账号校验业务响应: statusCode={SafeCode(statusCode)}, root-fields=[{DescribeFields(root)}], payload-fields=[{DescribeFields(payload)}]");
            if (statusCode != 200)
                throw new InvalidDataException(GetString(payload, "message") ?? "希沃账号校验失败。");

            var user = FindObject(payload, "UserInfo") ?? FindObject(payload, "userInfo") ??
                       FindObject(root, "UserInfo") ?? FindObject(root, "userInfo");
            if (user == null)
                throw new InvalidDataException("登录响应缺少用户信息。");

            var value = user.Value;
            Log($"账号校验用户对象: fields=[{DescribeFields(value)}]");
            // 完整响应（敏感字段的值打码、字段名保留）。
            // 用途：确认希沃是否提供了可用于长期续期的凭据字段（如 refreshToken / 过期时间），
            // 从而判断凭据失效后还有没有自动恢复的余地。
            Log($"账号校验完整响应(脱敏): {RedactForLog(value)}");
            var validatedToken = GetString(value, "tokenId") ?? "";
            if (string.IsNullOrWhiteSpace(validatedToken))
                throw new InvalidDataException("登录响应缺少账号令牌。");

            return new QrLoginOutcome
            {
                Token = validatedToken,
                UserInfo = new SeewoUserInfo
                {
                    AccountId = GetString(value, "resourceid") ?? "",
                    Uid = GetString(value, "resourceid") ?? "",
                    UserName = GetString(value, "userName") ?? "",
                    NickName = GetString(value, "cnName") ?? "",
                    RealName = GetString(value, "cnName") ?? "",
                    PhotoUrl = GetString(value, "photoUrl") ?? "",
                    Phone = GetString(value, "phone") ?? ""
                }
            };
        }

        private async Task<QrLoginStatus> SendStatusRequestAsync(string path, string sessionKey, bool scanned, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            AddAppCookies(request, sessionKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(125));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogPoll(scanned, $"HTTP {(int)response.StatusCode}, content-type={SafeMediaType(response.Content.Headers.ContentType?.MediaType)}");
                return new QrLoginStatus { State = QrLoginState.NetworkError };
            }

            using var document = await ReadJsonAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            var payload = GetResponsePayload(root);
            var code = GetInt(payload, "statusCode");
            var message = GetString(payload, "message") ?? "";
            var token = GetString(payload, "token") ?? "";
            LogPoll(scanned, $"HTTP {(int)response.StatusCode}, statusCode={SafeCode(code)}, token-present={!string.IsNullOrWhiteSpace(token)}, root-fields=[{DescribeFields(root)}], payload-fields=[{DescribeFields(payload)}]");

            if (!scanned)
            {
                return code switch
                {
                    200 => new QrLoginStatus { State = QrLoginState.WaitingForScan },
                    201 => new QrLoginStatus { State = QrLoginState.WaitingForConfirmation },
                    202 => new QrLoginStatus { State = QrLoginState.Completing, TemporaryToken = token },
                    300 => new QrLoginStatus { State = QrLoginState.Expired },
                    40322 => new QrLoginStatus { State = QrLoginState.Denied },
                    500 => new QrLoginStatus { State = QrLoginState.ProtocolError, ErrorMessage = message },
                    _ => new QrLoginStatus { State = QrLoginState.ProtocolError, ErrorMessage = message }
                };
            }

            return code switch
            {
                200 => new QrLoginStatus { State = QrLoginState.Completing, TemporaryToken = token },
                300 => new QrLoginStatus { State = QrLoginState.WaitingForConfirmation },
                501 => new QrLoginStatus { State = QrLoginState.Expired },
                40322 => new QrLoginStatus { State = QrLoginState.Denied },
                _ => new QrLoginStatus { State = QrLoginState.ProtocolError, ErrorMessage = message }
            };
        }

        private void AddAppCookies(HttpRequestMessage request, string sessionKey = null)
        {
            var values = new List<string> { $"x-auth-app={AppCode}", $"x-auth-brand={Brand}" };
            if (!string.IsNullOrWhiteSpace(sessionKey))
                values.Add($"qrkey={sessionKey}");
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", values));
        }

        private string FindCookie(string name)
        {
            return _cookies.GetCookies(Authority).Cast<Cookie>()
                .FirstOrDefault(cookie => string.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        }

        private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
        {
            var declaredLength = content.Headers.ContentLength;
            if (declaredLength > maximumBytes)
                throw new InvalidDataException("服务器响应过大。");

            await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                    throw new InvalidDataException("服务器响应过大。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
        {
            var bytes = await ReadLimitedAsync(content, MaximumJsonBytes, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }

        private static JsonElement GetResponsePayload(JsonElement root)
        {
            if (TryGetProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Object)
                return data;
            return root;
        }

        private static int GetInt(JsonElement element, string name)
        {
            if (!TryGetProperty(element, name, out var value)) return int.MinValue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : int.MinValue;
        }

        private static string GetString(JsonElement element, string name)
        {
            return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static JsonElement? FindObject(JsonElement root, string name)
        {
            if (TryGetProperty(root, name, out var direct) && direct.ValueKind == JsonValueKind.Object) return direct;
            if (TryGetProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Object &&
                TryGetProperty(data, name, out var nested) && nested.ValueKind == JsonValueKind.Object) return nested;
            return null;
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private void LogPoll(bool scanned, string diagnostic)
        {
            var value = $"phase={(scanned ? "confirmation" : "scan")}, {diagnostic}";
            if (string.Equals(_lastPollDiagnostic, value, StringComparison.Ordinal)) return;
            _lastPollDiagnostic = value;
            Log("二维码轮询响应: " + value);
        }

        private static string DescribeFields(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return element.ValueKind.ToString();
            return string.Join(",", element.EnumerateObject()
                .Select(property => IsSensitiveField(property.Name) ? property.Name + "=<redacted>" : property.Name));
        }

        /// <summary>
        /// 把 JSON 序列化成日志文本：敏感字段的值替换为 &lt;redacted&gt;，
        /// 其它字段连同取值一起保留，超长字符串截断。字段名始终保留。
        /// </summary>
        private static string RedactForLog(JsonElement element)
        {
            try
            {
                var sb = new StringBuilder();
                AppendRedacted(element, sb);
                var text = sb.ToString();
                return text.Length > 4000 ? text[..4000] + "…(截断)" : text;
            }
            catch
            {
                return "<序列化失败>";
            }
        }

        private static void AppendRedacted(JsonElement element, StringBuilder sb)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    var first = true;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"').Append(property.Name).Append("\":");
                        if (IsSensitiveField(property.Name)) sb.Append("\"<redacted>\"");
                        else AppendRedacted(property.Value, sb);
                    }
                    sb.Append('}');
                    break;

                case JsonValueKind.Array:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        AppendRedacted(item, sb);
                    }
                    sb.Append(']');
                    break;

                case JsonValueKind.String:
                    var str = element.GetString() ?? "";
                    // 长字符串多半是标识符，截断显示但保留长度信息
                    sb.Append('"').Append(str.Length > 40 ? str[..12] + "…(len=" + str.Length + ")" : str).Append('"');
                    break;

                default:
                    sb.Append(element.GetRawText());
                    break;
            }
        }

        private static bool IsSensitiveField(string name)
        {
            return name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("phone", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("qrkey", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType)) return "<missing>";
            return mediaType.Length <= 80 ? mediaType : mediaType.Substring(0, 80);
        }

        private static string SafeLength(long? length) => length.HasValue ? length.Value.ToString() : "<unknown>";
        private static string SafeCode(int code) => code == int.MinValue ? "<missing>" : code.ToString();
        private void Log(string message) => LogMessage?.Invoke("[QR HTTP] " + message);

        public void Dispose() => _http.Dispose();
    }
}
