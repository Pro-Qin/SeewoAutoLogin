using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

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
        /// <summary>
        /// 便携版更新包直链：优先 Portable.zip（目录版必须整包替换），没有就用单文件版。
        /// 便携版走自替换流程，不经过安装器。
        /// </summary>
        public string PortableUrl { get; set; } = "";
        /// <summary>命中的更新源（用于界面展示）</summary>
        public string Source { get; set; } = "";
        /// <summary>
        /// 安装包的 SHA256（取自同一 Release 的 SHA256SUMS.txt，小写十六进制）。
        /// 拿不到时保持空字符串，不影响原有流程；下载时传给 DownloadAccelerator 做强校验。
        /// </summary>
        public string Sha256 { get; set; } = "";
        /// <summary>
        /// 便携版更新包的 SHA256。便携版下载的是另一个文件（整包 zip 或单文件 exe），
        /// 不能拿安装包的哈希去校验它 —— 否则每次都会失败。
        /// </summary>
        public string PortableSha256 { get; set; } = "";
        /// <summary>SHA256SUMS.txt 的资源地址（API 返回时带出来，否则按发布规则推导）</summary>
        internal string Sha256SumsUrl { get; set; } = "";
    }
}
