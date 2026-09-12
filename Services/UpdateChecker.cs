using System;
using System.Collections.Generic;
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

        /// <summary>单个响应体读取上限（1MB），防止异常源返回超大内容</summary>
        private const int MaxResponseBytes = 1024 * 1024;

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
                Notes = root.TryGetProperty("body", out var bodyElement) ? Truncate(bodyElement.GetString(), 1000) : "",
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

        /// <summary>限长读取响应体：先看 Content-Length，再按流累计校验，超过 1MB 直接失败</summary>
        private static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage response, Action<string> log,
            CancellationToken cancellationToken)
        {
            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxResponseBytes)
            {
                log?.Invoke($"[Update] 响应体过大（{declared.Value} 字节 > {MaxResponseBytes} 字节），已放弃该更新源");
                throw new InvalidOperationException($"响应体过大（{declared.Value} 字节，上限 {MaxResponseBytes} 字节）");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken);
                if (read <= 0) break;

                if (buffer.Length + read > MaxResponseBytes)
                {
                    log?.Invoke($"[Update] 响应体超过 {MaxResponseBytes} 字节上限，已中止读取");
                    throw new InvalidOperationException($"响应体超过 {MaxResponseBytes} 字节上限");
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

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "…";
        }
    }
}
