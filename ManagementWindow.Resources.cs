using System.Text.RegularExpressions;

namespace SeewoAutoLogin
{
    public partial class ManagementWindow
    {
        /// <summary>脚本标签：内联兜底按它的顺序逐个取出前端资源。</summary>
        private static readonly Regex ScriptTagPattern = new(
            "<script[^>]*src=\"(?<src>[^\"]+)\"[^>]*>\\s*</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string GetEmbedded(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            var full = asm.GetManifestResourceNames().FirstOrDefault(n => n == name || n.EndsWith(name));
            if (full == null) throw new InvalidOperationException($"Embedded resource not found: {name}");
            using var s = asm.GetManifestResourceStream(full);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>
        /// 兜底渲染：本地资源释放失败（杀软拦截、目录不可写）时，用 NavigateToString 内联整页。
        ///
        /// 前端已按职责拆成多个脚本（frontend/js/*.js），只内联入口文件会得到一个「页面在、脚本全死」的白板，
        /// 所以这里照着 index.html 的 script 顺序把每个脚本都内联进去 —— 顺序即依赖顺序，与拆分的约定一致。
        /// 缺一个文件时只跳过它并留日志：少一个模块也不该让兜底页整页空白。
        /// </summary>
        private string BuildInlineHtml()
        {
            var html = GetEmbedded("SeewoAutoLogin.frontend.index.html");
            var css = GetEmbedded("SeewoAutoLogin.frontend.styles.css");

            var scripts = new StringBuilder();
            foreach (Match match in ScriptTagPattern.Matches(html))
            {
                var src = match.Groups["src"].Value;
                var resourceName = "SeewoAutoLogin.frontend." + src.Replace('/', '.').Replace('\\', '.');
                try
                {
                    scripts.Append("<script>").Append(GetEmbedded(resourceName)).AppendLine("</script>");
                }
                catch (Exception ex)
                {
                    _app?.WriteDiagnosticLog($"[WebView2] 内联脚本缺失: {src} - {ex.Message}");
                }
            }

            // 外链脚本在内联模式下没有可用的 base URL，必须整体去掉，否则浏览器会去 https://seewo-v.../js/ 取
            html = ScriptTagPattern.Replace(html, "");
            html = html.Replace("</head>", "<style>" + css + "</style></head>");
            return html.Replace("</body>", scripts + "</body>");
        }
    }
}
