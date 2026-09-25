using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SeewoAutoLogin.Services
{
    internal sealed class UpdateInfo
    {
        /// <summary>形如 1.8.0</summary>
        public string Version { get; set; } = "";
        /// <summary>形如 v1.8.0</summary>
        public string Tag { get; set; } = "";
        public string Notes { get; set; } = "";
        public string PageUrl { get; set; } = "";
        /// <summary>安装包直链（优先）</summary>
        public string SetupUrl { get; set; } = "";
        /// <summary>单文件版直链</summary>
        public string ExeUrl { get; set; } = "";
        /// <summary>命中的更新源（用于界面展示）</summary>
        public string Source { get; set; } = "";
        /// <summary>
        /// 安装包的 SHA256（取自同一 Release 的 SHA256SUMS.txt，小写十六进制）。
        /// 拿不到时保持空字符串，不影响原有流程；下载时传给 DownloadAccelerator 做强校验。
        /// </summary>
        public string Sha256 { get; set; } = "";
        /// <summary>SHA256SUMS.txt 的资源地址（API 返回时带出来，否则按发布规则推导）</summary>
        internal string Sha256SumsUrl { get; set; } = "";
    }

    /// <summary>
    /// 版本更新检查。默认更新源只有 GitHub 官方 API，失败时用 jsDelivr 数据接口兜底（只拿版本号，下载指向发布页）。
    /// 出于安全考虑不再使用第三方 GitHub 代理：它们可以改写返回的 JSON，从而把下载地址指向任意域名。
    /// 所有下载直链都必须通过 <see cref="IsTrustedDownloadUrl"/> 校验，否则清空并引导用户去发布页手动下载。
    /// </summary>
    internal static class UpdateChecker
    {
        public const string RepoOwner = "Pro-Qin";
        public const string RepoName = "SeewoAutoLogin";

        public static string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

        /// <summary>
        /// 解析「用哪个地址打开发布页」：国内直连 github.com 常打不开，
        /// 复用下载加速器的探测能力优先返回可达的镜像地址，全部不可达时退回原地址。
        /// 传入具体 tag 页面（如 .../releases/tag/v1.10.1）用户就能直接落到对应版本。
        /// </summary>
        public static async Task<string> ResolveBestPageUrlAsync(string officialPageUrl, Action<string> log,
            CancellationToken cancellationToken)
        {
            var url = string.IsNullOrWhiteSpace(officialPageUrl) ? ReleasesPageUrl : officialPageUrl.Trim();
            if (!IsTrustedDownloadUrl(url))
            {
                log?.Invoke($"[Update] 发布页地址不可信，改用官方发布页: {url}");
                url = ReleasesPageUrl;
            }

            try
            {
                var ranked = await DownloadAccelerator.RankCandidatesAsync(url, log, cancellationToken)
                    .ConfigureAwait(false);
                if (ranked.Count > 0)
                {
                    if (!string.Equals(ranked[0], url, StringComparison.OrdinalIgnoreCase))
                        log?.Invoke($"[Update] 官方地址可能打不开，改用镜像发布页: {ranked[0]}");
                    return ranked[0];
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 发布页镜像探测失败（改用官方地址）: {ex.Message}");
            }

            return url;
        }

        /// <summary>单个响应体读取上限（1MB），防止异常源返回超大内容</summary>
        private const int MaxResponseBytes = 1024 * 1024;

        /// <summary>SHA256SUMS.txt 的读取上限（64KB），它只是一份哈希清单</summary>
        private const int MaxSumsBytes = 64 * 1024;

        /// <summary>校验清单文件名（由 release.yml 在打包步骤后生成并上传）</summary>
        private const string Sha256SumsFileName = "SHA256SUMS.txt";

        /// <summary>默认更新源（按顺序自动降级，可用自定义源覆盖）；只保留 GitHub 官方接口，第三方代理镜像已全部移除</summary>
        private static readonly string[] DefaultSources =
        {
            "https://api.github.com/repos/{0}/{1}/releases/latest"
        };

        /// <summary>可信下载主机白名单（忽略大小写）</summary>
        private static readonly string[] TrustedDownloadHosts =
        {
            "github.com",
            "www.github.com",
            "objects.githubusercontent.com",
            "raw.githubusercontent.com",
            "codeload.github.com",
            "release-assets.githubusercontent.com",
            "github-releases.githubusercontent.com",
            "cdn.jsdelivr.net"
        };

        /// <summary>可信下载域名的子域后缀（必须以 "." 开头，避免 evilgithub.com / evilgithubusercontent.com 这类绕过）</summary>
        private static readonly string[] TrustedDownloadSuffixes =
        {
            ".github.com",
            ".githubusercontent.com"
        };

        /// <summary>
        /// 判断下载地址是否可信：必须是合法的 https 绝对地址，且主机命中白名单（含受信任域名的子域）。
        /// </summary>
        public static bool IsTrustedDownloadUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

            var host = uri.Host;
            foreach (var trusted in TrustedDownloadHosts)
            {
                if (string.Equals(host, trusted, StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (var suffix in TrustedDownloadSuffixes)
            {
                // 必须以 ".域名" 结尾：evilgithub.com 不匹配 ".github.com"，因此无法冒充
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        public static async Task<UpdateInfo> CheckLatestAsync(string overrideSource, Action<string> log,
            CancellationToken cancellationToken)
        {
            var failures = new List<string>();

            foreach (var template in BuildSources(overrideSource))
            {
                try
                {
                    // 自定义源先校验（https + {0}/{1} 占位符），格式错误只记为一次失败，不影响后续源降级
                    if (!TryBuildSourceUrl(template, out var url, out var reason))
                    {
                        var label = HostOf((template ?? "").Trim());
                        failures.Add($"{label}: {reason}");
                        log?.Invoke($"[Update] 跳过无效更新源 {label}：{reason}");
                        continue;
                    }

                    using var client = CreateClient();
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        failures.Add($"{HostOf(url)}: HTTP {(int)response.StatusCode}");
                        continue;
                    }

                    var body = await ReadBodyLimitedAsync(response, log, cancellationToken);
                    var info = ParseRelease(body, url, log);
                    if (info == null || string.IsNullOrWhiteSpace(info.Version))
                    {
                        failures.Add($"{HostOf(url)}: 响应解析失败");
                        continue;
                    }

                    // 有 SHA256SUMS.txt 就顺带把安装包哈希取回来（失败只写日志，不影响检查更新）
                    await AttachSha256Async(info, log, cancellationToken);

                    log?.Invoke($"[Update] 更新源 {info.Source} 命中，最新版本 {info.Tag}");
                    return info;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{HostOf((template ?? "").Trim())}: {ex.Message}");
                }
            }

            // 兜底：jsDelivr 数据接口（国内可直连，只能拿到版本号）
            try
            {
                var info = await CheckViaJsDelivrAsync(log, cancellationToken);
                if (info != null)
                {
                    await AttachSha256Async(info, log, cancellationToken);

                    log?.Invoke($"[Update] 兜底源 jsDelivr 命中，最新版本 {info.Tag}");
                    return info;
                }
                failures.Add("jsDelivr: 无可用版本");
            }
            catch (Exception ex)
            {
                failures.Add($"jsDelivr: {ex.Message}");
            }

            throw new InvalidOperationException("所有更新源均不可用（" + string.Join("；", failures) + "）");
        }

        private static IEnumerable<string> BuildSources(string overrideSource)
        {
            if (!string.IsNullOrWhiteSpace(overrideSource))
            {
                var custom = overrideSource.Trim();
                // 只填了基地址（如 https://api.github.com）时按 GitHub API 规范补全路径
                yield return custom.IndexOf("{0}", StringComparison.Ordinal) >= 0
                    ? custom
                    : custom.TrimEnd('/') + "/repos/{0}/{1}/releases/latest";
            }

            foreach (var source in DefaultSources) yield return source;
        }

        /// <summary>
        /// 校验并生成实际的更新源地址：必须含 {0}/{1} 占位符、格式化结果必须是合法的 https 绝对地址。
        /// </summary>
        private static bool TryBuildSourceUrl(string template, out string url, out string reason)
        {
            url = "";
            reason = "";

            if (string.IsNullOrWhiteSpace(template))
            {
                reason = "更新源为空";
                return false;
            }

            var text = template.Trim();
            if (text.IndexOf("{0}", StringComparison.Ordinal) < 0 ||
                text.IndexOf("{1}", StringComparison.Ordinal) < 0)
            {
                reason = "更新源必须同时包含 {0} 与 {1} 占位符";
                return false;
            }

            // string.Format 放在 try 内（由调用方捕获），格式串异常不会中断整条降级链
            var formatted = string.Format(text, RepoOwner, RepoName);
            if (!Uri.TryCreate(formatted, UriKind.Absolute, out var uri))
            {
                reason = "更新源不是合法的绝对地址";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"更新源必须使用 https（当前 {uri.Scheme}）";
                return false;
            }

            // 更新源只允许官方域名与白名单镜像（GitHub 官方 / jsDelivr 等），
            // 防止自定义源返回伪造的版本号与下载地址；不在白名单时回退到默认官方源。
            if (!IsTrustedDownloadUrl(formatted))
            {
                reason = $"更新源域名不在允许列表（仅 GitHub 官方与 jsDelivr 等镜像）：{uri.Host}";
                return false;
            }

            url = formatted;
            return true;
        }

        private static UpdateInfo ParseRelease(string json, string url, Action<string> log)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var pageUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(pageUrl) || !IsTrustedDownloadUrl(pageUrl))
            {
                if (!string.IsNullOrWhiteSpace(pageUrl))
                    log?.Invoke($"[Update] 忽略不可信的发布页地址，改用官方发布页：{pageUrl}");
                pageUrl = ReleasesPageUrl;
            }

            var info = new UpdateInfo
            {
                Tag = tag ?? "",
                Version = NormalizeVersion(tag),
                Notes = root.TryGetProperty("body", out var bodyElement)
                    ? ExtractChangelog(bodyElement.GetString())
                    : "",
                PageUrl = pageUrl,
                Source = HostOf(url)
            };

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var setupAssets = new List<KeyValuePair<string, string>>();
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    var download = asset.TryGetProperty("browser_download_url", out var downloadElement)
                        ? downloadElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(download)) continue;

                    // browser_download_url 不可原样信任：只接受 https + 白名单主机，否则丢弃该资源
                    if (!IsTrustedDownloadUrl(download))
                    {
                        log?.Invoke($"[Update] 忽略不可信下载地址（{HostOf(download.Trim())}）：{download.Trim()}");
                        continue;
                    }

                    // 校验清单：记下地址，稍后单独限长下载（拿不到就留空，不影响更新流程）
                    if (string.Equals(name, Sha256SumsFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        info.Sha256SumsUrl = download;
                        continue;
                    }

                    if (name.StartsWith("SeewoAutoLogin_Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        setupAssets.Add(new KeyValuePair<string, string>(name, download));
                    }
                    else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.ExeUrl))
                    {
                        info.ExeUrl = download;
                    }
                }

                // 多个安装包时优先选「未内置 WebView2」的轻量版（体积小得多），内置版留在发布页给需要的用户
                var preferred = setupAssets.FirstOrDefault(
                    asset => asset.Key.IndexOf("WithWebView2", StringComparison.OrdinalIgnoreCase) < 0);
                if (string.IsNullOrEmpty(preferred.Value))
                    preferred = setupAssets.Count > 0 ? setupAssets[0] : default;
                if (!string.IsNullOrEmpty(preferred.Value)) info.SetupUrl = preferred.Value;
            }

            // API 未返回资源（或被裁剪）时按发布规则推导安装包直链
            if (string.IsNullOrEmpty(info.SetupUrl) && !string.IsNullOrWhiteSpace(info.Version))
                info.SetupUrl = BuildSetupUrl(info.Tag, info.Version);

            // 直链最终校验：不通过就置空，并让用户自己去发布页下载（消费端会用 Process.Start 打开该地址）
            if (!string.IsNullOrEmpty(info.SetupUrl) && !IsTrustedDownloadUrl(info.SetupUrl))
            {
                log?.Invoke($"[Update] 下载地址未通过可信校验，已置空并改用发布页手动下载：{info.SetupUrl}");
                info.SetupUrl = "";
                info.PageUrl = ReleasesPageUrl;
            }

            if (!string.IsNullOrEmpty(info.ExeUrl) && !IsTrustedDownloadUrl(info.ExeUrl))
            {
                log?.Invoke($"[Update] 单文件版下载地址未通过可信校验，已忽略：{info.ExeUrl}");
                info.ExeUrl = "";
            }

            return info;
        }

        private static async Task<UpdateInfo> CheckViaJsDelivrAsync(Action<string> log, CancellationToken cancellationToken)
        {
            using var client = CreateClient();
            var url = $"https://data.jsdelivr.com/v1/packages/gh/{RepoOwner}/{RepoName}";
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await ReadBodyLimitedAsync(response, log, cancellationToken));
            if (!document.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return null;

            string latest = null;
            foreach (var entry in versions.EnumerateArray())
            {
                var version = NormalizeVersion(entry.TryGetProperty("version", out var element) ? element.GetString() : null);
                if (string.IsNullOrWhiteSpace(version)) continue;
                if (latest == null || CompareVersions(version, latest) > 0) latest = version;
            }

            if (latest == null) return null;

            var tag = "v" + latest;
            return new UpdateInfo
            {
                Tag = tag,
                Version = latest,
                PageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{tag}",
                SetupUrl = BuildSetupUrl(tag, latest),
                Source = "jsDelivr"
            };
        }

        /// <summary>
        /// 尝试从同一 Release 的 SHA256SUMS.txt 里取出「最终选中的安装包」对应的哈希，填进 <see cref="UpdateInfo.Sha256"/>。
        /// 这是纯增强步骤：文件不存在、拉取失败、解析不到都只写日志，绝不影响更新检查本身。
        /// </summary>
        private static async Task AttachSha256Async(UpdateInfo info, Action<string> log, CancellationToken cancellationToken)
        {
            if (info == null) return;

            try
            {
                var targetName = PickHashTargetName(info);
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    log?.Invoke("[Update] 未确定安装包文件名，跳过 SHA256SUMS.txt");
                    return;
                }

                var url = !string.IsNullOrWhiteSpace(info.Sha256SumsUrl)
                    ? info.Sha256SumsUrl
                    : (!string.IsNullOrWhiteSpace(info.Tag)
                        ? $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{info.Tag}/{Sha256SumsFileName}"
                        : "");

                if (string.IsNullOrWhiteSpace(url) || !IsTrustedDownloadUrl(url))
                {
                    log?.Invoke("[Update] 没有可信的 SHA256SUMS.txt 地址，跳过哈希校验");
                    return;
                }

                var text = await FetchSumsTextAsync(url, log, cancellationToken);
                if (string.IsNullOrWhiteSpace(text)) return;

                var hash = ParseSha256Sums(text, targetName);
                if (string.IsNullOrWhiteSpace(hash))
                {
                    log?.Invoke($"[Update] SHA256SUMS.txt 中没有 {targetName} 的记录，本次不做哈希校验");
                    return;
                }

                info.Sha256 = hash;
                log?.Invoke($"[Update] 已获取 {targetName} 的 SHA256：{hash}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] 获取 SHA256SUMS.txt 失败（忽略，不影响更新）：{ex.Message}");
            }
        }

        /// <summary>
        /// 读取 SHA256SUMS.txt：先直连（快）；直连不通（国内很常见）再交给
        /// <see cref="DownloadAccelerator"/> 按网络状况动态挑源。
        /// </summary>
        private static async Task<string> FetchSumsTextAsync(string url, Action<string> log,
            CancellationToken cancellationToken)
        {
            try
            {
                using var client = CreateClient();
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return await ReadBodyLimitedAsync(response, log, cancellationToken, MaxSumsBytes);

                // 404 是「这个 Release 确实没有清单」，换加速通道也还是 404，直接跳过，别白折腾
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    log?.Invoke("[Update] 该 Release 没有 SHA256SUMS.txt（HTTP 404），跳过哈希校验");
                    return "";
                }

                log?.Invoke($"[Update] SHA256SUMS.txt 直连返回 HTTP {(int)response.StatusCode}，改用加速通道");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Update] SHA256SUMS.txt 直连失败（{ex.Message}），改用加速通道");
            }

            // 走多源动态选路；清单本身不需要哈希校验，所以 expectedSha256 传 null
            var local = await DownloadAccelerator.DownloadAsync(url, null, null, log, cancellationToken);

            var file = new FileInfo(local);
            if (file.Length > MaxSumsBytes)
            {
                log?.Invoke($"[Update] SHA256SUMS.txt 过大（{file.Length} 字节 > {MaxSumsBytes} 字节），已忽略");
                return "";
            }

            return await File.ReadAllTextAsync(local, Encoding.UTF8, cancellationToken);
        }

        /// <summary>取「最终会被下载的那个包」的文件名：优先安装包，退而取单文件版</summary>
        private static string PickHashTargetName(UpdateInfo info)
        {
            var name = FileNameOf(info.SetupUrl);
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return FileNameOf(info.ExeUrl);
        }

        private static string FileNameOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "";

            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var index = path.LastIndexOf('/');
            return index >= 0 ? path.Substring(index + 1) : path;
        }

        /// <summary>
        /// 解析 SHA256SUMS.txt，返回指定文件名对应的哈希（忽略大小写）。找不到返回空串。
        /// 兼容 "hash  name"、"hash *name"、制表符分隔以及 # 注释行。
        /// </summary>
        private static string ParseSha256Sums(string text, string fileName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fileName)) return "";

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var hash = parts[0].Trim().ToLowerInvariant();
                if (hash.Length != 64) continue;

                var name = parts[parts.Length - 1].TrimStart('*').Trim();
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;

                return hash;
            }

            return "";
        }

        /// <summary>限长读取响应体：先看 Content-Length，再按流累计校验，超过上限直接失败（默认 1MB，SHA256SUMS.txt 用 64KB）</summary>
        private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, Action<string> log,
            CancellationToken cancellationToken, int maxBytes = MaxResponseBytes)
        {
            if (maxBytes <= 0) maxBytes = MaxResponseBytes;

            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > maxBytes)
            {
                log?.Invoke($"[Update] 响应体过大（{declared.Value} 字节 > {maxBytes} 字节），已放弃该更新源");
                throw new InvalidOperationException($"响应体过大（{declared.Value} 字节，上限 {maxBytes} 字节）");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken);
                if (read <= 0) break;

                if (buffer.Length + read > maxBytes)
                {
                    log?.Invoke($"[Update] 响应体超过 {maxBytes} 字节上限，已中止读取");
                    throw new InvalidOperationException($"响应体超过 {maxBytes} 字节上限");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string BuildSetupUrl(string tag, string version)
            => $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/SeewoAutoLogin_Setup_v{version}.exe";

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler();
            // 代理软件未运行但系统里残留代理设置时，检查更新会整体失败；这里自动回退直连。
            NetworkRoute.ConfigureHandler(handler, new Uri("https://api.github.com/"));
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SeewoAutoLogin");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>去掉 v 前缀并取前若干段数字，例如 v1.8.0-beta → 1.8.0</summary>
        public static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            var match = Regex.Match(text, @"\d+(\.\d+){0,3}");
            return match.Success ? match.Value : "";
        }

        /// <summary>版本号比较：left &gt; right 返回 1，相等 0，小于 -1</summary>
        public static int CompareVersions(string left, string right)
        {
            var a = ParseParts(left);
            var b = ParseParts(right);
            var length = Math.Max(a.Length, b.Length);
            for (var i = 0; i < length; i++)
            {
                var x = i < a.Length ? a[i] : 0;
                var y = i < b.Length ? b[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static int[] ParseParts(string version)
            => NormalizeVersion(version).Split('.')
                .Where(part => part.Length > 0)
                .Select(part => int.TryParse(part, out var value) ? value : 0)
                .ToArray();

        private static string HostOf(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

                /// <summary>
        /// 从完整的发布说明里只取「本次更新」那一段。
        ///
        /// 完整说明是写给下载页访客看的：先讲三个产物怎么选，再列本次改动，
        /// 最后是许可与免责声明。软件内不需要这些 —— 用户已经在用这个程序，
        /// 他只想知道这一版改了什么，所以这里把其余部分全部丢掉。
        /// </summary>
        private static string ExtractChangelog(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";

            var section = System.Text.RegularExpressions.Regex.Match(
                body,
                @"^##\s*本次更新[^\n]*\n(?<c>[\s\S]*?)(?=\n##\s|\n---\s*\n\s*##|\z)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            if (section.Success)
                return section.Groups["c"].Value.Trim().TrimEnd('-').Trim();

            var items = body.Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.TrimStart().StartsWith("- "))
                .ToList();
            return items.Count > 0 ? string.Join("\n", items) : Truncate(body.Trim(), 600);
        }
private static string Truncate(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "…";
        }
    }

    /// <summary>
    /// 安装包启动前的校验结果。只有 <see cref="Verified"/> 为 true 才允许启动，
    /// 只有 <see cref="CanSilentInstall"/> 为 true 才允许带静默参数启动。
    /// </summary>
    internal sealed class UpdatePackageCheck
    {
        /// <summary>文件路径合法：位于本程序自己的下载目录内、是 exe、且确实存在</summary>
        public bool PathTrusted { get; set; }
        /// <summary>文件 SHA256 与官方 SHA256SUMS.txt 取到的值一致</summary>
        public bool HashVerified { get; set; }
        /// <summary>这个 Release 确实提供了可用的官方 SHA256（用于区分「哈希不符」与「根本没有哈希可比」）</summary>
        public bool HashAvailable { get; set; }
        /// <summary>是否为 Inno Setup 安装包（SeewoAutoLogin_Setup_*.exe），只有它认静默参数</summary>
        public bool IsSetupPackage { get; set; }
        /// <summary>校验通过后的绝对路径（未通过时可能是空串或原始输入）</summary>
        public string FullPath { get; set; } = "";
        public string ExpectedSha256 { get; set; } = "";
        public string ActualSha256 { get; set; } = "";
        /// <summary>是否要求 Authenticode 签名（配置了可信签名指纹时为 true）</summary>
        public bool SignatureRequired { get; set; }
        /// <summary>Authenticode 签名有效且签名者指纹命中白名单</summary>
        public bool SignatureVerified { get; set; }
        /// <summary>签名者证书指纹（验证通过时填充）</summary>
        public string SignerThumbprint { get; set; } = "";
        /// <summary>未通过时的原因（中文，可直接展示给用户）</summary>
        public string Reason { get; set; } = "";

        /// <summary>
        /// 路径、哈希以及（配置了签名白名单时的）Authenticode 签名都通过：文件可信，可以交给用户手动安装。
        /// </summary>
        public bool Verified => PathTrusted && HashVerified && (!SignatureRequired || SignatureVerified);

        /// <summary>可信的 Inno Setup 安装包：可以静默安装并在安装前让本程序退出</summary>
        public bool CanSilentInstall => Verified && IsSetupPackage;
    }

    /// <summary>
    /// 静默升级的最后一段：校验安装包、启动 Inno Setup 静默安装。
    ///
    /// 安全约束（这里是「本程序主动执行下载来的 exe」唯一的入口，不允许绕过）：
    ///   • 只允许运行 <see cref="DownloadAccelerator.DownloadDirectory"/>（%TEMP%\SeewoAutoLogin\update）里的 .exe；
    ///   • 启动前必须重新计算 SHA256，并与 <see cref="UpdateInfo.Sha256"/>（源于官方 SHA256SUMS.txt）逐位一致；
    ///   • 拿不到官方哈希时不做静默安装，退回让用户手动安装 —— 宁可少自动化，也不运行无法校验的文件。
    /// </summary>
    internal static class UpdateInstaller
    {
        /// <summary>Inno Setup 静默安装参数：静默安装 / 关闭占用文件的程序 / 不自动重启系统</summary>
        public const string SilentArguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART";

        /// <summary>Inno Setup 产物文件名前缀（setup.iss: SeewoAutoLogin_Setup_v{版本}[_WithWebView2].exe）</summary>
        private const string SetupFileNamePrefix = "SeewoAutoLogin_Setup";

        /// <summary>允许启动安装包的目录（下载目录本身，不接受子目录以外的任何位置）</summary>
        public static string DownloadDirectory => DownloadAccelerator.DownloadDirectory;

        /// <summary>
        /// 校验安装包：路径合法（下载目录内 + exe + 存在）+ SHA256 与官方清单一致。
        /// 计算哈希会读整个文件，请放到后台线程调用。
        /// </summary>
        public static UpdatePackageCheck VerifyPackage(string localPath, string expectedSha256, Action<string> log)
        {
            var check = new UpdatePackageCheck();
            var input = (localPath ?? "").Trim();

            string full;
            try
            {
                full = input.Length == 0 ? "" : Path.GetFullPath(input);
            }
            catch (Exception ex)
            {
                check.Reason = "安装包路径非法：" + ex.Message;
                log?.Invoke($"[Update] 安装包路径非法，拒绝启动：{input}（{ex.Message}）");
                return check;
            }

            if (full.Length == 0)
            {
                check.Reason = "安装包路径为空";
                log?.Invoke("[Update] 安装包路径为空，拒绝启动");
                return check;
            }

            check.FullPath = full;

            // 路径前缀校验：只认下载目录（含分隔符），避免被替换成任意程序或目录外的同名文件
            var directory = Path.GetFullPath(DownloadDirectory);
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                check.Reason = $"安装包不在下载目录内（仅允许 {directory}）";
                log?.Invoke($"[Update] 安装包不在下载目录内，拒绝启动：{full}");
                return check;
            }

            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                check.Reason = "更新包不是 exe 文件";
                log?.Invoke($"[Update] 更新包不是 exe，拒绝启动：{full}");
                return check;
            }

            if (!File.Exists(full))
            {
                check.Reason = "安装包不存在（可能已被清理）";
                log?.Invoke($"[Update] 安装包不存在，拒绝启动：{full}");
                return check;
            }

            check.PathTrusted = true;
            check.IsSetupPackage = Path.GetFileNameWithoutExtension(full)
                .StartsWith(SetupFileNamePrefix, StringComparison.OrdinalIgnoreCase);

            // SHA256：必须与检查更新时从官方 SHA256SUMS.txt 取到的值一致
            var expected = NormalizeHash(expectedSha256);
            if (expected == null)
            {
                check.Reason = "没有官方 SHA256（该 Release 缺少 SHA256SUMS.txt），无法校验安装包";
                log?.Invoke($"[Update] 缺少官方 SHA256，拒绝静默安装（{Path.GetFileName(full)}）");
                return check;
            }

            check.HashAvailable = true;
            check.ExpectedSha256 = expected;
            try
            {
                check.ActualSha256 = DownloadAccelerator.ComputeSha256(full);
            }
            catch (Exception ex)
            {
                check.Reason = "计算 SHA256 失败：" + ex.Message;
                log?.Invoke($"[Update] 安装包 SHA256 计算失败，拒绝启动：{ex.Message}");
                return check;
            }

            if (!string.Equals(check.ActualSha256, expected, StringComparison.OrdinalIgnoreCase))
            {
                check.Reason = $"SHA256 不一致（期望 {Short(expected)}，实际 {Short(check.ActualSha256)}）";
                log?.Invoke($"[Update] 安装包 SHA256 校验失败，拒绝启动：{full}；期望 {expected}，实际 {check.ActualSha256}");
                return check;
            }

            check.HashVerified = true;

            // Authenticode：配置了可信签名指纹时，必须签名有效且证书指纹命中白名单，否则拒绝启动。
            check.SignatureRequired = AuthenticodeVerifier.HasConfiguredTrust;
            if (check.SignatureRequired)
            {
                if (!AuthenticodeVerifier.Verify(full, out var thumbprint, out var sigReason))
                {
                    check.Reason = "Authenticode 签名校验失败：" + sigReason;
                    log?.Invoke($"[Update] 安装包签名校验失败，拒绝启动：{full}；{sigReason}");
                    return check;
                }
                check.SignatureVerified = true;
                check.SignerThumbprint = thumbprint;
                log?.Invoke($"[Update] 安装包签名校验通过：{Path.GetFileName(full)}；签名者指纹={thumbprint}");
            }
            else
            {
                log?.Invoke("[Update] 未配置可信签名指纹，跳过 Authenticode 校验（仍依赖 SHA256）");
            }

            log?.Invoke($"[Update] 安装包校验通过：{Path.GetFileName(full)}；SHA256={check.ActualSha256}");
            return check;
        }

        /// <summary>
        /// 启动 Inno Setup 静默安装。只接受 <see cref="VerifyPackage"/> 的校验结果，
        /// 返回 true 表示安装程序已拉起（调用方应随后主动退出本程序）。
        /// </summary>
        public static bool StartSilentInstall(UpdatePackageCheck check, Action<string> log)
        {
            if (check == null || !check.Verified)
            {
                log?.Invoke($"[Update] 拒绝启动静默安装：{(check == null ? "没有校验结果" : check.Reason)}");
                return false;
            }

            if (!check.IsSetupPackage)
            {
                log?.Invoke($"[Update] {Path.GetFileName(check.FullPath)} 不是 Inno Setup 安装包，不支持静默安装参数");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = check.FullPath,
                    Arguments = SilentArguments,
                    WorkingDirectory = Path.GetDirectoryName(check.FullPath) ?? DownloadDirectory,
                    // 安装包要求管理员权限：走 Shell 启动，未提权时由系统弹 UAC，
                    // 而不是像 UseShellExecute=false 那样直接抛「需要提升」异常。
                    UseShellExecute = true
                };

                var process = Process.Start(psi);
                var pid = "未知";
                try { if (process != null && process.Id > 0) pid = process.Id.ToString(); } catch { }

                log?.Invoke($"[Update] 已启动静默安装程序：{Path.GetFileName(check.FullPath)} {SilentArguments}（pid={pid}）");
                return true;
            }
            catch (Exception ex)
            {
                // 常见于用户在 UAC 弹窗上点了「否」（Win32Exception 1223）
                log?.Invoke($"[Update] 启动静默安装失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>规范化期望哈希：空或不是 64 位十六进制都返回 null（视为「没有可用的官方哈希」）</summary>
        private static string NormalizeHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var text = value.Trim();
            if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7).Trim();
            if (text.Length != 64) return null;

            foreach (var c in text)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return null;
            }

            return text.ToLowerInvariant();
        }

        private static string Short(string hash)
            => string.IsNullOrEmpty(hash) || hash.Length <= 12 ? hash : hash.Substring(0, 12) + "…";
    }
}
