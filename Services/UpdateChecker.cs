using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    /// 版本更新检查。默认更新源为 GitHub API + 多个国内镜像，逐个尝试，第一个可用即返回；
    /// 全部 API 源失败时用 jsDelivr 数据接口兜底（只拿版本号，下载指向发布页）。
    /// </summary>
    internal static class UpdateChecker
    {
        public const string RepoOwner = "Pro-Qin";
        public const string RepoName = "SeewoAutoLogin";

        public static string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

        /// <summary>默认更新源（备用源按顺序自动降级，可用自定义源覆盖）</summary>
        private static readonly string[] DefaultSources =
        {
            "https://api.github.com/repos/{0}/{1}/releases/latest",
            "https://api.kkgithub.com/repos/{0}/{1}/releases/latest",
            "https://gh-proxy.com/https://api.github.com/repos/{0}/{1}/releases/latest",
            "https://ghproxy.net/https://api.github.com/repos/{0}/{1}/releases/latest",
            "https://ghfast.top/https://api.github.com/repos/{0}/{1}/releases/latest",
            "https://mirror.ghproxy.com/https://api.github.com/repos/{0}/{1}/releases/latest"
        };

        public static async Task<UpdateInfo> CheckLatestAsync(string overrideSource, Action<string> log,
            CancellationToken cancellationToken)
        {
            var failures = new List<string>();

            foreach (var template in BuildSources(overrideSource))
            {
                var url = string.Format(template, RepoOwner, RepoName);
                try
                {
                    using var client = CreateClient();
                    using var response = await client.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        failures.Add($"{HostOf(url)}: HTTP {(int)response.StatusCode}");
                        continue;
                    }

                    var info = ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken), url);
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
                    failures.Add($"{HostOf(url)}: {ex.Message}");
                }
            }

            // 兜底：jsDelivr 数据接口（国内可直连，只能拿到版本号）
            try
            {
                var info = await CheckViaJsDelivrAsync(cancellationToken);
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
                yield return custom.Contains("{0}")
                    ? custom
                    : custom.TrimEnd('/') + "/repos/{0}/{1}/releases/latest";
            }

            foreach (var source in DefaultSources) yield return source;
        }

        private static UpdateInfo ParseRelease(string json, string url)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var info = new UpdateInfo
            {
                Tag = tag ?? "",
                Version = NormalizeVersion(tag),
                Notes = root.TryGetProperty("body", out var bodyElement) ? Truncate(bodyElement.GetString(), 1000) : "",
                PageUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : ReleasesPageUrl,
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

            // API 未返回资源（或被镜像裁剪）时按发布规则推导安装包直链
            if (string.IsNullOrEmpty(info.SetupUrl) && !string.IsNullOrWhiteSpace(info.Version))
                info.SetupUrl = BuildSetupUrl(info.Tag, info.Version);

            return info;
        }

        private static async Task<UpdateInfo> CheckViaJsDelivrAsync(CancellationToken cancellationToken)
        {
            using var client = CreateClient();
            var url = $"https://data.jsdelivr.com/v1/packages/gh/{RepoOwner}/{RepoName}";
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
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
