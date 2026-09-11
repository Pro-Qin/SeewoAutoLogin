using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 把嵌入的前端资源释放到本地目录，供 WebView2 通过虚拟主机映射加载。
    ///
    /// 相比旧的 <c>NavigateToString(拼接后的大字符串)</c>：
    ///  · 没有 NavigateToString 的 2MB 字符串上限；
    ///  · CSS/JS 由浏览器并行解析（不再依赖先拼接再整体解析），首屏更快；
    ///  · 资源可被 WebView2 缓存，命中时无需重新解析。
    /// </summary>
    internal static class WebContentProvisioner
    {
        private static readonly string[] Files = { "index.html", "styles.css", "app.js" };

        /// <summary>
        /// 虚拟主机名带版本号：升级后 URL 变化，避免 WebView2 命中旧版本的 HTML/CSS/JS 缓存。
        /// </summary>
        public static string VirtualHostName
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                var id = version == null
                    ? "dev"
                    : $"{version.Major}-{version.Minor}-{version.Build}";
                return $"seewo-v{id}.example";
            }
        }

        /// <summary>前端资源目录（LOCALAPPDATA 下，避免 Program Files 写权限问题）。</summary>
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAutoLogin", "web");

        /// <summary>入口页面 URL（配合 SetVirtualHostNameToFolderMapping 使用）。</summary>
        public static string EntryUrl => $"https://{VirtualHostName}/index.html";

        /// <summary>
        /// 释放前端资源，返回 Web 根目录。内容未变化时不写盘（每次启动只做几次小文件读取）。
        /// </summary>
        public static string Provision()
        {
            var assembly = Assembly.GetExecutingAssembly();
            Directory.CreateDirectory(Root);

            foreach (var file in Files)
            {
                var resourceName = "SeewoAutoLogin.frontend." + file;
                var fullName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n == resourceName || n.EndsWith(resourceName, StringComparison.Ordinal));
                if (fullName == null) throw new InvalidOperationException($"Embedded resource not found: {resourceName}");

                string content;
                using (var stream = assembly.GetManifestResourceStream(fullName))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    content = reader.ReadToEnd();
                }

                var destination = Path.Combine(Root, file);
                if (File.Exists(destination))
                {
                    try
                    {
                        if (File.ReadAllText(destination) == content) continue;
                    }
                    catch
                    {
                        // 读取失败则按需要重写
                    }
                }

                File.WriteAllText(destination, content, new UTF8Encoding(false));
            }

            return Root;
        }
    }
}
