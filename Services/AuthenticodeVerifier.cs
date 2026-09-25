using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 用 WinVerifyTrust 验证 exe 的 Authenticode 签名，并把签名者证书指纹与白名单比对。
    ///
    /// 配置方式：把代码签名证书的指纹（SHA-256 或 SHA-1）填进
    /// <see cref="AllowedSignerThumbprints"/>。填了之后，更新安装包必须同时满足：
    ///   - 文件有有效且受信任的 Authenticode 签名；
    ///   - 签名者证书指纹命中白名单；
    ///   - SHA256 与官方 SHA256SUMS.txt 一致。
    /// 白名单留空时退化为只校验 SHA256，兼容还没有证书的构建。
    /// </summary>
    internal static class AuthenticodeVerifier
    {
        /// <summary>
        /// 允许的代码签名证书指纹（SHA-256 或 SHA-1 都支持，忽略大小写、空格和冒号）。
        /// 例：private static readonly string[] AllowedSignerThumbprints = { "0123456789ABCDEF..." };
        /// </summary>
        private static readonly string[] AllowedSignerThumbprints =
        {
            // TODO: 申请代码签名证书后，把证书指纹填在这里（可填多个）。
        };

        /// <summary>是否配置了签名白名单。未配置时跳过 Authenticode 校验，仅依赖 SHA256。</summary>
        public static bool HasConfiguredTrust =>
            AllowedSignerThumbprints.Any(t =>
                !string.IsNullOrWhiteSpace(t) &&
                !t.TrimStart().StartsWith("//", StringComparison.Ordinal));

        /// <summary>验证文件签名并比对指纹；返回 false 时 reason 可直接展示给用户。</summary>
        public static bool Verify(string filePath, out string signerThumbprint, out string reason)
        {
            signerThumbprint = "";
            reason = "";
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                reason = "文件不存在";
                return false;
            }

            try
            {
                if (!WinVerifyTrustValid(filePath, out reason)) return false;

                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
                signerThumbprint = cert.Thumbprint ?? "";
                var sha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
                var normalizedSha1 = Normalize(signerThumbprint);
                var normalizedSha256 = Normalize(sha256);
                var matched = AllowedSignerThumbprints
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(Normalize)
                    .Any(t => t.Length > 0 && (t == normalizedSha1 || t == normalizedSha256));

                if (!matched)
                {
                    reason = $"签名者证书不在白名单（{cert.Subject}；指纹 {signerThumbprint}）";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "读取签名者证书失败：" + ex.Message;
                return false;
            }
        }

        private static string Normalize(string thumbprint) =>
            (thumbprint ?? "").Replace(" ", "").Replace(":", "").Trim().ToUpperInvariant();

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionIgnore = 0;
        private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        private static bool WinVerifyTrustValid(string filePath, out string reason)
        {
            reason = "";
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = IntPtr.Zero,
                dwStateAction = WtdStateActionIgnore,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WtdCacheOnlyUrlRetrieval,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };

            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                data.pFile = fileInfoPtr;
                Marshal.StructureToPtr(data, dataPtr, false);

                var action = WinTrustActionGenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
                if (result == 0) return true;

                reason = DescribeWinVerifyTrustError(result);
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        private static string DescribeWinVerifyTrustError(uint result)
        {
            switch (result)
            {
                case 0x800B0100: return "文件没有 Authenticode 签名";
                case 0x800B0101: return "签名证书已过期";
                case 0x800B0109: return "签名证书的根证书不受信任";
                case 0x800B010A: return "无法构建签名证书链";
                case 0x800B010C: return "签名证书已被吊销";
                case 0x800B0110: return "签名证书用途不匹配";
                case 0x800B0111: return "签名证书被显式设为不受信任";
                case 0x80096005: return "无法验证签名时间戳";
                case 0x80096010: return "文件被篡改或签名摘要无效";
                default: return $"WinVerifyTrust 返回 0x{result:X8}";
            }
        }
    }
}