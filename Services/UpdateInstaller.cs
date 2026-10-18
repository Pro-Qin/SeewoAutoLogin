using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace SeewoAutoLogin.Services
{
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
        /// <summary>
        /// Inno Setup 的静默安装参数。
        ///
        /// 必须用 /VERYSILENT，不能用 /SILENT：后者只是"不需要点下一步"，**仍然会弹出安装进度窗口**。
        /// 老师正在上课时冷不丁冒出一个安装器窗口，比更新本身还扰民。/VERYSILENT 全程不显示界面，
        /// /SUPPRESSMSGBOXES 让安装器自己吞掉提示框，/SP- 跳过"准备安装"那一屏。
        /// </summary>
        public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /SP-";

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
