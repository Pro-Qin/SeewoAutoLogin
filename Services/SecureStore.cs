using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 使用 Windows DPAPI 加密/解密敏感数据。
    /// 加密结果带 "dpapi:" 前缀，避免把恰好是合法 Base64 的明文误判为密文。
    /// </summary>
    public static class SecureStore
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("com.seewo-autologin/pwd/v2");

        /// <summary>
        /// 是否为当前格式（带前缀）的 DPAPI 密文
        /// </summary>
        public static bool IsEncrypted(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// 加密字符串为 "dpapi:" + Base64。失败时抛出异常（不静默回退明文）。
        /// </summary>
        public static string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            var data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(data, AppEntropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("DPAPI 加密失败，为避免明文落盘已中止保存", ex);
            }
            return Prefix + Convert.ToBase64String(encrypted);
        }

        /// <summary>
        /// 解密带前缀的 DPAPI 密文。数据损坏或前缀缺失时抛出异常。
        /// </summary>
        public static string Decrypt(string encryptedValue)
        {
            if (string.IsNullOrEmpty(encryptedValue)) return "";
            if (!IsEncrypted(encryptedValue))
                throw new InvalidOperationException("数据不是当前格式的加密内容");
            var data = Convert.FromBase64String(encryptedValue.Substring(Prefix.Length));
            var decrypted = ProtectedData.Unprotect(data, AppEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }

        /// <summary>
        /// 兼容旧版无前缀的 DPAPI 密文：成功返回明文，失败返回 null（调用方按明文处理）。
        /// </summary>
        public static string TryDecryptLegacy(string value)
        {
            if (string.IsNullOrEmpty(value) || IsEncrypted(value)) return null;
            try
            {
                var data = Convert.FromBase64String(value);
                var decrypted = ProtectedData.Unprotect(data, AppEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return null;
            }
        }
    }
}
