using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 更新包下载加速器：按当前网络状况动态挑选可用的下载通道。
    ///
    /// 背景：release 直链是 https://github.com/... 的官方地址，国内网络实测经常完全连不通。
    /// 这里把「官方直链」与「常见 GitHub 加速前缀 + 官方直链」组成候选列表，
    /// 先并发探测每个候选（带 Range 的 GET，4 秒超时），记录「可达 + 响应耗时」，
    /// 按耗时从快到慢排序后依次下载；某个源失败、超时或哈希校验不通过就换下一个源，
    /// 只有全部源都失败才抛异常。
    ///
    /// 安全约束（避免把任意 URL 当成下载源）：
    ///   • 候选地址必须是合法的 https 绝对地址；
    ///   • 官方地址的主机必须在 <see cref="OfficialHosts"/> / <see cref="OfficialHostSuffixes"/> 白名单内；
    ///   • 加速前缀必须是 <see cref="AcceleratorPrefixes"/> 里配置过的域名。
    ///   • expectedSha256 非空时，只有校验通过的文件才会返回给调用方。
    /// </summary>
    public static class DownloadAccelerator
    {
        /// <summary>
        /// GitHub 加速前缀（可配置）。数组顺序只作为「探测全部失败」时的兜底尝试顺序，
        /// 正常情况下实际顺序由探测耗时决定。必须是 https 且不能带查询串/路径。
        /// </summary>
        public static string[] AcceleratorPrefixes =
        {
            "https://ghfast.top/",
            "https://gh-proxy.com/",
            "https://ghproxy.net/",
            "https://mirror.ghproxy.com/",
            "https://ghproxy.cc/"
        };

        /// <summary>官方可信下载主机（忽略大小写）</summary>
        private static readonly string[] OfficialHosts =
        {
            "github.com",
            "www.github.com",
            "objects.githubusercontent.com",
            "raw.githubusercontent.com",
            "codeload.github.com",
            "release-assets.githubusercontent.com",
            "github-releases.githubusercontent.com"
        };

        /// <summary>官方可信域名的子域后缀（必须以 "." 开头，避免 evilgithub.com 这类绕过）</summary>
        private static readonly string[] OfficialHostSuffixes =
        {
            ".github.com",
            ".githubusercontent.com"
        };

        /// <summary>单次探测超时（4 秒）：探测只读 64KB 就断开，慢于 4 秒的源直接判为不可用</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

        /// <summary>下载时等待响应头的超时（15 秒）：连不上的源不必等满 300 秒</summary>
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

        /// <summary>单个源的下载总预算（300 秒），超时就换下一个源</summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(300);

        /// <summary>探测请求读取的字节数（Range: bytes=0-65535）</summary>
        private const int ProbeRangeBytes = 64 * 1024;

        private const int BufferSize = 81920;

        /// <summary>候选源数量上限（官方 + 加速），防止配置被写坏后并发探测过多</summary>
        private const int MaxCandidates = 12;

        /// <summary>下载目录：%TEMP%\SeewoAutoLogin\update</summary>
        public static string DownloadDirectory
            => Path.Combine(Path.GetTempPath(), "SeewoAutoLogin", "update");

        /// <summary>
        /// 生成候选下载源列表：第一项是官方直链本身，其余是「加速前缀 + 原始 URL」。
        /// officialUrl 不可信（非 https 或主机不在白名单）时抛 <see cref="ArgumentException"/>。
        /// </summary>
        public static IReadOnlyList<string> BuildCandidates(string officialUrl)
        {
            if (!IsTrustedOfficialUrl(officialUrl))
                throw new ArgumentException("官方下载地址必须是 https 且主机在白名单内：" + (officialUrl ?? "(null)"),
                    nameof(officialUrl));

            var official = officialUrl.Trim();
            var list = new List<string> { official };

            foreach (var prefix in AcceleratorPrefixes ?? Array.Empty<string>())
            {
                var normalized = NormalizePrefix(prefix);
                if (normalized == null) continue;
                var candidate = normalized + official;
                if (!IsAllowedCandidateUrl(candidate)) continue;
                if (list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(candidate);
                if (list.Count >= MaxCandidates) break;
            }

            return list;
        }

        /// <summary>
        /// 并发探测候选源并按响应耗时从快到慢排序，返回可用的候选地址。
        /// 全部探测失败时退回 <see cref="BuildCandidates"/> 的原始顺序。
        /// </summary>
        public static async Task<IReadOnlyList<string>> RankCandidatesAsync(string officialUrl, Action<string> log,
            CancellationToken ct)
        {
            var candidates = BuildCandidates(officialUrl);
            log?.Invoke($"[下载] 候选下载源 {candidates.Count} 个：" + string.Join("、", candidates.Select(Describe)));

            using var client = CreateClient(Timeout.InfiniteTimeSpan);
            var tasks = candidates.Select(url => ProbeAsync(client, url, ct)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            var reachable = new List<ProbeResult>();
            foreach (var result in results.OrderBy(r => r.Reachable ? 0 : 1).ThenBy(r => r.ElapsedMs))
            {
                if (result.Reachable)
                {
                    log?.Invoke($"探测 {Describe(result.Url)}: {result.ElapsedMs}ms");
                    reachable.Add(result);
                }
                else
                {
                    log?.Invoke($"探测 {Describe(result.Url)}: 不可达（{result.Error}），已剔除");
                }
            }

            if (reachable.Count == 0)
            {
                // 探测全灭（例如代理把探测请求拦了但下载能过）时，不要直接放弃，按原顺序逐个试
                log?.Invoke("[下载] 全部候选源探测失败，按原顺序逐个尝试");
                return candidates;
            }

            return reachable.OrderBy(r => r.ElapsedMs).Select(r => r.Url).ToList();
        }

        /// <summary>
        /// 下载更新包：按探测排序后的源依次尝试，返回本地文件的完整路径。
        /// </summary>
        /// <param name="officialUrl">官方 https 直链（如 GitHub release 的 browser_download_url）</param>
        /// <param name="expectedSha256">期望的 SHA256（可为空；非空时必须校验通过才返回）</param>
        /// <param name="progress">进度回调（total 为 -1 表示未知）</param>
        /// <param name="log">日志回调（可为 null）</param>
        /// <param name="ct">取消令牌</param>
        public static async Task<string> DownloadAsync(string officialUrl, string expectedSha256,
            IProgress<(long received, long total)> progress, Action<string> log, CancellationToken ct)
        {
            var fileName = FileNameOf(officialUrl);
            var directory = DownloadDirectory;
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, fileName);

            var normalizedHash = NormalizeExpectedHash(expectedSha256);
            if (!string.IsNullOrEmpty(expectedSha256) && normalizedHash == null)
                throw new ArgumentException("expectedSha256 不是合法的 SHA256（应为 64 位十六进制）", nameof(expectedSha256));

            var ordered = await RankCandidatesAsync(officialUrl, log, ct).ConfigureAwait(false);

            var failures = new List<string>();
            foreach (var url in ordered)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 同名残留文件先删掉，避免续写/覆盖出半个包
                    TryDelete(target);

                    log?.Invoke($"改用源 {Describe(url)} 下载：{fileName}");
                    var sw = Stopwatch.StartNew();

                    string actualHash;
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        // 单个源的下载总预算 300 秒，超时就换下一个源
                        cts.CancelAfter(DownloadTimeout);
                        actualHash = await DownloadOneAsync(url, target, normalizedHash != null, progress, cts.Token)
                            .ConfigureAwait(false);
                    }

                    sw.Stop();
                    var size = new FileInfo(target).Length;

                    if (normalizedHash != null && !string.Equals(actualHash, normalizedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        log?.Invoke($"哈希校验失败，换源（期望 {Short(normalizedHash)}，实际 {Short(actualHash)}）");
                        TryDelete(target);
                        failures.Add($"{Describe(url)}: 哈希校验失败");
                        continue;
                    }

                    if (normalizedHash != null) log?.Invoke($"哈希校验通过：{actualHash}");
                    log?.Invoke($"[下载] 完成：{target}（{size} 字节，耗时 {sw.ElapsedMilliseconds}ms，源 {Describe(url)}）");
                    return target;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(target);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(target);
                    failures.Add($"{Describe(url)}: {ex.Message}");
                    log?.Invoke($"[下载] 源 {Describe(url)} 失败：{ex.Message}，换下一个源");
                }
            }

            throw new InvalidOperationException("所有下载源均不可用（" + string.Join("；", failures) + "）");
        }

        /// <summary>计算文件的 SHA256（小写十六进制）</summary>
        public static string ComputeSha256(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(stream));
        }

        /// <summary>单个源的下载实现：流式写盘 + 增量算哈希，返回实际 SHA256（不需要时返回 null）</summary>
        private static async Task<string> DownloadOneAsync(string url, string target, bool computeHash,
            IProgress<(long received, long total)> progress, CancellationToken ct)
        {
            using var client = CreateClient(Timeout.InfiniteTimeSpan);

            // 区分「连不上 / 迟迟不返回响应头」和「下载太慢」：前者 15 秒就判死换源，
            // 后者交给调用方的 300 秒总预算，否则探测全灭时会死死卡在一个不可达的源上。
            HttpResponseMessage response;
            using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headCts.CancelAfter(ResponseTimeout);
                try
                {
                    response = await client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"源 {ResponseTimeout.TotalSeconds:0} 秒内未返回响应");
                }
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                return await ReadAndHashAsync(response, target, computeHash, progress, ct).ConfigureAwait(false);
            }
        }

        /// <summary>把响应体流式写盘（需要时同步算增量哈希），返回实际 SHA256；computeHash 为 false 时返回 null</summary>
        private static async Task<string> ReadAndHashAsync(HttpResponseMessage response, string target, bool computeHash,
            IProgress<(long received, long total)> progress, CancellationToken ct)
        {
            var total = response.Content.Headers.ContentLength ?? -1;
            progress?.Report((0, total));

            using var hash = computeHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            long received = 0;

            using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash?.AppendData(buffer, 0, read);
                    received += read;
                    progress?.Report((received, total));
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            // Content-Length 对得上才算下载完整（加速通道偶尔会提前断流）
            if (total >= 0 && received != total)
                throw new InvalidOperationException($"下载不完整（{received}/{total} 字节）");

            if (received <= 0) throw new InvalidOperationException("下载内容为空");

            return hash == null ? null : ToHex(hash.GetHashAndReset());
        }

        private sealed class ProbeResult
        {
            public string Url { get; set; } = "";
            public bool Reachable { get; set; }
            public long ElapsedMs { get; set; } = long.MaxValue;
            public string Error { get; set; } = "";
        }

        /// <summary>探测单个候选源：带 Range 的 GET，只读到第一个数据块就断开，记录耗时</summary>
        private static async Task<ProbeResult> ProbeAsync(HttpClient client, string url, CancellationToken ct)
        {
            var result = new ProbeResult { Url = url };
            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProbeTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, ProbeRangeBytes - 1);

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    result.Error = $"HTTP {(int)response.StatusCode}";
                    return result;
                }

                // 有些代理会返回 200 + 一页错误 HTML，这里必须真的读到数据才算可达
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var buffer = new byte[1024];
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    result.Error = "无数据";
                    return result;
                }

                sw.Stop();
                result.Reachable = true;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result.Error = $"超时 {ProbeTimeout.TotalSeconds:0} 秒";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>候选地址是否可信：https + 官方主机白名单或已配置的加速域名</summary>
        private static bool IsAllowedCandidateUrl(string url)
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

            if (IsOfficialHost(uri.Host)) return true;

            foreach (var prefix in AcceleratorPrefixes ?? Array.Empty<string>())
            {
                var normalized = NormalizePrefix(prefix);
                if (normalized == null) continue;
                if (!Uri.TryCreate(normalized, UriKind.Absolute, out var prefixUri)) continue;
                if (string.Equals(uri.Host, prefixUri.Host, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>官方下载地址是否可信（https + 官方主机白名单）</summary>
        public static bool IsTrustedOfficialUrl(string url)
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            return IsOfficialHost(uri.Host);
        }

        private static bool IsOfficialHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            foreach (var trusted in OfficialHosts)
            {
                if (string.Equals(host, trusted, StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (var suffix in OfficialHostSuffixes)
            {
                // 必须以 ".域名" 结尾：evilgithub.com 不匹配 ".github.com"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>把加速前缀规范化成 "https://host/" 形式；不合法（非 https / 带路径 / 解析失败）返回 null</summary>
        private static string NormalizePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return null;
            var text = prefix.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrWhiteSpace(uri.Host)) return null;
            if (uri.AbsolutePath != "/" && uri.AbsolutePath != "") return null;

            return "https://" + uri.Host + "/";
        }

        /// <summary>日志/展示用的源名称：官方直连标出来，加速源显示域名</summary>
        private static string Describe(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url ?? "";
            return IsOfficialHost(uri.Host) ? $"{uri.Host}（官方直连）" : $"{uri.Host}（加速）";
        }

        /// <summary>取 URL 末段作为文件名，并过滤非法字符（防路径穿越）</summary>
        private static string FileNameOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("下载地址为空", nameof(url));

            var path = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.AbsolutePath : url.Trim();
            var name = Uri.UnescapeDataString(path).Replace('\\', '/');
            var index = name.LastIndexOf('/');
            if (index >= 0) name = name.Substring(index + 1);

            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.Trim();

            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                throw new ArgumentException("无法从下载地址解析出文件名：" + url, nameof(url));

            return name;
        }

        /// <summary>规范化期望哈希：空返回 null；不是 64 位十六进制也返回 null（由调用方判定非法）</summary>
        private static string NormalizeExpectedHash(string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256)) return null;

            var text = expectedSha256.Trim();
            if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7).Trim();
            if (text.Length != 64) return null;

            foreach (var c in text)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return null;
            }

            return text.ToLowerInvariant();
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler();
            // 代理软件未运行但系统里残留代理设置时，下载会整体失败；这里自动回退直连。
            NetworkRoute.ConfigureHandler(handler, new Uri("https://github.com/"));
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SeewoAutoLogin");
            return client;
        }

        private static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string digits = "0123456789abcdef";
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        private static string Short(string hash)
            => string.IsNullOrEmpty(hash) || hash.Length <= 12 ? hash : hash.Substring(0, 12) + "…";

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删除失败不影响后续换源尝试（同名文件会以 Create 方式覆盖）
            }
        }
    }
}
