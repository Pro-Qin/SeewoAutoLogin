using System;
using System.Security.Cryptography;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 应用设置口令的哈希与校验。
    /// - 新格式：PBKDF2-SHA256（12 万次迭代）+ 随机盐，哈希值本身再用 DPAPI 加密后落盘，
    ///   使配置文件被复制到其它机器/账户后无法离线爆破。
    /// - 兼容旧格式（单轮 SHA-256 + 盐）并在校验成功后由调用方升级。
    /// - 连续失败会临时锁定，抵御本地暴力尝试。
    /// </summary>
    internal static class PasswordService
    {
        private const int Iterations = 120_000;
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int MaxFailuresBeforeLock = 5;

        private static readonly object Gate = new object();
        private static int _failedAttempts;
        private static DateTime _lockUntilUtc = DateTime.MinValue;

        /// <summary>生成新格式哈希。返回的 hash 已用 DPAPI 包裹。</summary>
        internal static void Create(string password, out string hash, out string salt)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("口令不能为空", nameof(password));
            var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
            var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), saltBytes, Iterations, HashAlgorithmName.SHA256, HashSize);
            salt = Convert.ToBase64String(saltBytes);
            hash = SecureStore.Encrypt(Convert.ToBase64String(hashBytes));
        }

        /// <summary>校验口令；兼容旧格式（旧格式成功时把 upgraded 置为 true，调用方应重新保存配置）</summary>
        internal static bool Verify(string password, string storedHash, string storedSalt, out bool upgraded)
        {
            upgraded = false;
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash)) return false;

            if (IsLockedOut(out _)) return false;

            bool ok;
            if (SecureStore.IsEncrypted(storedHash))
            {
                string plainHash;
                try { plainHash = SecureStore.Decrypt(storedHash); }
                catch { return false; }

                byte[] expected;
                try { expected = Convert.FromBase64String(plainHash); }
                catch { return false; }

                byte[] saltBytes;
                try { saltBytes = Convert.FromBase64String(storedSalt ?? ""); }
                catch { return false; }
                if (saltBytes.Length == 0) return false;

                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password), saltBytes, Iterations, HashAlgorithmName.SHA256, expected.Length);
                ok = CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            else
            {
                // 旧格式：SHA256(password + salt) 的 Base64
                using var sha = SHA256.Create();
                var legacy = Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(password + (storedSalt ?? ""))));
                ok = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(legacy), Encoding.UTF8.GetBytes(storedHash));
                if (ok) upgraded = true;
            }

            lock (Gate)
            {
                if (ok)
                {
                    _failedAttempts = 0;
                    _lockUntilUtc = DateTime.MinValue;
                }
                else
                {
                    _failedAttempts++;
                    if (_failedAttempts >= MaxFailuresBeforeLock)
                    {
                        var minutes = Math.Min(5, 1 + (_failedAttempts - MaxFailuresBeforeLock) / 5);
                        _lockUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                    }
                }
            }
            return ok;
        }

        internal static bool IsLockedOut(out int secondsRemaining)
        {
            lock (Gate)
            {
                var remaining = (_lockUntilUtc - DateTime.UtcNow).TotalSeconds;
                if (remaining > 0)
                {
                    secondsRemaining = (int)Math.Ceiling(remaining);
                    return true;
                }
                secondsRemaining = 0;
                return false;
            }
        }
    }
}
