using System;
using System.IO;
using SeewoAutoLogin.Services;
using Xunit;

namespace SeewoAutoLogin.Tests
{
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
            chars[chars.Length - 1] = chars[chars.Length - 1] == 'A' ? 'B' : 'A';
            Assert.ThrowsAny<Exception>(() => SecureStore.Decrypt("v2:" + new string(chars)));
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