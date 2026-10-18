using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace SeewoAutoLogin.Services
{
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
}
