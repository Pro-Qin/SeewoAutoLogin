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
        private const string ResourcePrefix = "SeewoAutoLogin.frontend.";

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

            foreach (var fullName in assembly.GetManifestResourceNames())
            {
                if (!fullName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                var relative = ToRelativePath(fullName.Substring(ResourcePrefix.Length));
                if (string.IsNullOrEmpty(relative)) continue;

                byte[] bytes;
                using (var stream = assembly.GetManifestResourceStream(fullName))
                {
                    if (stream == null) throw new InvalidOperationException($"Embedded resource not found: {fullName}");
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }

                var destination = Path.Combine(Root, relative);
                var dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(destination))
                {
                    try
                    {
                        var existing = File.ReadAllBytes(destination);
                        if (existing.Length == bytes.Length && existing.AsSpan().SequenceEqual(bytes)) continue;
                    }
                    catch
                    {
                        // 读取失败则按需要重写
                    }
                }

                // 用字节写入：教程截图是二进制资源，不能按文本处理
                File.WriteAllBytes(destination, bytes);
            }

            return Root;
        }

        /// <summary>
        /// 把嵌入资源名还原成相对路径："assets.tutorial-tray.png" → "assets\tutorial-tray.png"。
        /// 目录分隔符在资源名里是 '.'，最后一段是扩展名。
        /// 注意：文件名本身请勿包含点（如 app.min.js），否则会被误判为目录。
        /// </summary>
        private static string ToRelativePath(string resourceRelative)
        {
            var parts = resourceRelative.Split('.');
            if (parts.Length < 2) return null;

            var extension = "." + parts[parts.Length - 1];
            var withoutExtension = parts.Take(parts.Length - 1).ToArray();
            if (withoutExtension.Length == 0) return null;

            var fileName = withoutExtension[withoutExtension.Length - 1] + extension;
            var directories = withoutExtension.Take(withoutExtension.Length - 1).ToArray();
            return directories.Length == 0 ? fileName : Path.Combine(Path.Combine(directories), fileName);
        }
    }
}
