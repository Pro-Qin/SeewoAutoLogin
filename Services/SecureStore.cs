using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 凭据加密。
    ///
    /// v2 格式（当前）：
    ///   - 首次运行时生成 32 字节随机主密钥，用 DPAPI CurrentUser 包裹后写入
    ///     %LOCALAPPDATA%\SeewoAutoLogin\credential.key；
    ///   - 密码用 AES-256-GCM 加密，输出 v2: + Base64(nonce || ciphertext || tag)。
    ///   旧版本 exe 只知道固定 AppEntropy，没有 credential.key，解不开 v2 数据。
    ///
    /// 兼容旧格式：
    ///   - dpapi: 前缀的旧版 DPAPI 密文仍可解密，启动时会迁移到 v2；
    ///   - 无前缀的历史 DPAPI 密文/明文由 TryDecryptLegacy 处理。
    /// </summary>
    public static class SecureStore
    {
        private const string LegacyPrefix = "dpapi:";
        private const string V2Prefix = "v2:";
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;

        private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("com.seewo-autologin/pwd/v2");
        private static readonly object KeyGate = new object();
        private static byte[] _cachedMasterKey;

        /// <summary>测试用：覆盖主密钥文件路径；生产保持 null。</summary>
        internal static string KeyPathOverride { get; set; }

        /// <summary>测试用：清除主密钥缓存。</summary>
        internal static void ResetCacheForTests()
        {
            lock (KeyGate) { _cachedMasterKey = null; }
        }

        public static bool IsEncrypted(string value)
            => !string.IsNullOrEmpty(value) &&
               (value.StartsWith(LegacyPrefix, StringComparison.Ordinal) ||
                value.StartsWith(V2Prefix, StringComparison.Ordinal));

        public static bool IsLegacyEncrypted(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(LegacyPrefix, StringComparison.Ordinal);

        public static bool IsV2Encrypted(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(V2Prefix, StringComparison.Ordinal);

        /// <summary>加密为 v2 格式。失败时抛出异常（不静默回退明文）。</summary>
        public static string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try
            {
                var key = GetOrCreateMasterKey();
                var nonce = CryptoCompat.RandomBytes(NonceSize);
                var plainBytes = Encoding.UTF8.GetBytes(plaintext);
                var cipher = new byte[plainBytes.Length];
                var tag = new byte[TagSize];
                AesGcmCompat.Encrypt(key, nonce, plainBytes, cipher, tag);

                var payload = new byte[NonceSize + cipher.Length + TagSize];
                Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
                Buffer.BlockCopy(cipher, 0, payload, NonceSize, cipher.Length);
                Buffer.BlockCopy(tag, 0, payload, NonceSize + cipher.Length, TagSize);
                return V2Prefix + Convert.ToBase64String(payload);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("凭据加密失败，为避免明文落盘已中止保存", ex);
            }
        }

        /// <summary>解密 v2 或旧 dpapi: 格式。数据损坏、格式不支持时抛出异常。</summary>
        public static string Decrypt(string encryptedValue)
        {
            if (string.IsNullOrEmpty(encryptedValue)) return "";
            if (IsV2Encrypted(encryptedValue)) return DecryptV2(encryptedValue);
            if (IsLegacyEncrypted(encryptedValue)) return DecryptLegacy(encryptedValue);
            throw new InvalidOperationException("数据不是当前支持的加密格式");
        }

        private static string DecryptV2(string value)
        {
            var payload = Convert.FromBase64String(value.Substring(V2Prefix.Length));
            if (payload.Length < NonceSize + TagSize)
                throw new CryptographicException("v2 密文长度不合法");

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipher = new byte[payload.Length - NonceSize - TagSize];
            Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(payload, NonceSize, cipher, 0, cipher.Length);
            Buffer.BlockCopy(payload, NonceSize + cipher.Length, tag, 0, TagSize);

            var plain = new byte[cipher.Length];
            AesGcmCompat.Decrypt(GetOrCreateMasterKey(), nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }

        private static string DecryptLegacy(string value)
        {
            var data = Convert.FromBase64String(value.Substring(LegacyPrefix.Length));
            var decrypted = ProtectedData.Unprotect(data, LegacyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }

        /// <summary>兼容旧版无前缀的 DPAPI 密文：成功返回明文，失败返回 null（调用方按明文处理）。</summary>
        public static string TryDecryptLegacy(string value)
        {
            if (string.IsNullOrEmpty(value) || IsEncrypted(value)) return null;
            try
            {
                var data = Convert.FromBase64String(value);
                var decrypted = ProtectedData.Unprotect(data, LegacyEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return null;
            }
        }

        private static string KeyPath => !string.IsNullOrWhiteSpace(KeyPathOverride)
            ? KeyPathOverride
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeewoAutoLogin", "credential.key");

        private static byte[] GetOrCreateMasterKey()
        {
            if (_cachedMasterKey != null) return _cachedMasterKey;
            lock (KeyGate)
            {
                if (_cachedMasterKey != null) return _cachedMasterKey;

                var path = KeyPath;
                if (File.Exists(path))
                {
                    var protectedKey = File.ReadAllBytes(path);
                    var key = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
                    if (key.Length != KeySize)
                        throw new CryptographicException("凭据密钥长度不合法");
                    _cachedMasterKey = key;
                    return key;
                }

                var newKey = CryptoCompat.RandomBytes(KeySize);
                var protectedNewKey = ProtectedData.Protect(newKey, null, DataProtectionScope.CurrentUser);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var temp = path + ".tmp";
                File.WriteAllBytes(temp, protectedNewKey);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                TryHardenKeyFileAcl(path);

                _cachedMasterKey = newKey;
                return newKey;
            }
        }

        private static void TryHardenKeyFileAcl(string path)
        {
            try
            {
                var file = new FileInfo(path);
                var security = file.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
                var user = WindowsIdentity.GetCurrent().User;
                if (user != null)
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        user, FileSystemRights.FullControl, AccessControlType.Allow));
                }
                file.SetAccessControl(security);
            }
            catch
            {
                // ACL 加固失败不影响 DPAPI 保护本身
            }
        }
    }
}