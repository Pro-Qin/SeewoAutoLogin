using System;
using System.IO;
using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
    // 与 ConfigPackageApplyTests 共用同一个集合：两边都要改 SecureStore.KeyPathOverride
    // 这个全局静态字段，并行跑会互相踩（曾导致随机的 Encrypt_Decrypt_RoundTrips 失败）。
    [Collection("SecureStore")]
    public class SecureStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _keyPath;

        public SecureStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "SeewoAutoLogin.Tests", Guid.NewGuid().ToString("N"));
            _keyPath = Path.Combine(_dir, "credential.key");
            SecureStore.KeyPathOverride = _keyPath;
            SecureStore.ResetCacheForTests();
        }

        public void Dispose()
        {
            SecureStore.KeyPathOverride = null;
            SecureStore.ResetCacheForTests();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Encrypt_Decrypt_RoundTrips()
        {
            const string plain = "p@ssw0rd-中文-123";
            var encrypted = SecureStore.Encrypt(plain);
            Assert.StartsWith("v2:", encrypted);
            Assert.True(SecureStore.IsV2Encrypted(encrypted));
            Assert.Equal(plain, SecureStore.Decrypt(encrypted));
        }

        [Fact]
        public void Encrypt_Same_Plaintext_Produces_Different_Ciphertext()
        {
            Assert.NotEqual(SecureStore.Encrypt("same"), SecureStore.Encrypt("same"));
        }

        [Fact]
        public void Tampered_Ciphertext_Throws()
        {
            var encrypted = SecureStore.Encrypt("secret");
            var chars = encrypted.Substring(3).ToCharArray();
            chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
            Assert.ThrowsAny<Exception>(() => SecureStore.Decrypt("v2:" + new string(chars)));
        }

        [Fact]
        public void Broken_Master_Key_Is_Rebuilt_And_Old_Ciphertext_Becomes_Unreadable()
        {
            var oldCipher = SecureStore.Encrypt("old-secret");

            // 模拟「配置目录从另一台电脑搬过来」：key 文件在，但本机 DPAPI 解不开
            SecureStore.ResetCacheForTests();
            File.WriteAllBytes(_keyPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            SecureStore.ResetCacheForTests();

            // 自愈的第一目标：还能加密。否则用户连「重新录入密码」都存不进去，软件等于变砖
            var newCipher = SecureStore.Encrypt("new-secret");
            Assert.Equal("new-secret", SecureStore.Decrypt(newCipher));
            Assert.True(SecureStore.MasterKeyRecreated);

            // 坏钥匙改名留档，不删除
            Assert.NotEmpty(Directory.GetFiles(_dir, "credential.key.broken-*"));

            // 旧密文在新钥匙下解不开：这是预期结果，由 DecryptedPassword 捕获并提示重新录入
            Assert.ThrowsAny<Exception>(() => SecureStore.Decrypt(oldCipher));
        }

        [Fact]
        public void Legacy_Dpapi_Format_Is_Still_Decryptable()
        {
            var legacy = BuildLegacyCiphertext("legacy-secret");
            Assert.True(SecureStore.IsLegacyEncrypted(legacy));
            Assert.Equal("legacy-secret", SecureStore.Decrypt(legacy));
        }

        private static string BuildLegacyCiphertext(string plain)
        {
            var entropy = System.Text.Encoding.UTF8.GetBytes("com.seewo-autologin/pwd/v2");
            var data = System.Text.Encoding.UTF8.GetBytes(plain);
            var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
                data, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(protectedBytes);
        }
    }
}