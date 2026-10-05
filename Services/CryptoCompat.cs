using System;
using System.Security.Cryptography;
using System.Text;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 把 .NET Framework 4.8 缺失、而 .NET 8 内置的加密/编码 API 收敛到一处。
    /// 对应关系：RandomNumberGenerator.GetBytes(int)、Rfc2898DeriveBytes.Pbkdf2、
    /// CryptographicOperations.FixedTimeEquals、SHA256.HashData、Convert.ToHexString、Encoding.Latin1。
    /// 行为与 BCL 版本保持一致，保证主程序与 Win7 分支的密文格式、哈希值完全兼容。
    /// </summary>
    internal static class CryptoCompat
    {
        private const string HexAlphabet = "0123456789ABCDEF";

        private static readonly Encoding Latin1Encoding = Encoding.GetEncoding(28591);

        /// <summary>等价 RandomNumberGenerator.GetBytes(int)。</summary>
        public static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        /// <summary>等价 .NET 6+ 的 Rfc2898DeriveBytes.Pbkdf2（PBKDF2-HMAC-SHA256）。</summary>
        public static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int outputBytes)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            {
                return kdf.GetBytes(outputBytes);
            }
        }

        /// <summary>等价 CryptographicOperations.FixedTimeEquals：比较耗时不随首个不同字节的位置变化。</summary>
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;

            var diff = 0;
            for (var i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }

        /// <summary>等价 SHA256.HashData。</summary>
        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        /// <summary>等价 Convert.ToHexString（大写十六进制）。</summary>
        public static string ToHexString(byte[] bytes)
        {
            if (bytes == null) return null;

            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = HexAlphabet[bytes[i] >> 4];
                chars[i * 2 + 1] = HexAlphabet[bytes[i] & 0x0F];
            }
            return new string(chars);
        }

        /// <summary>等价 Encoding.Latin1（ISO-8859-1，代码页 28591，net48 自带）。</summary>
        public static Encoding Latin1
        {
            get { return Latin1Encoding; }
        }
    }
}
