using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 账号的可迁移导出与导入。
    ///
    /// 本机的账号密码是用 DPAPI 加密的，密钥绑定当前 Windows 账户 —— 换台电脑就解不开，
    /// 所以导出时先把密码解出来，再用「用户自己设的口令」重新加密成一个可以搬走的文件。
    /// 导入端用同一个口令解密，落到本机时再改回 DPAPI 加密。
    ///
    /// 加密：PBKDF2-SHA256（20 万次）派生 32 字节密钥 + AES-256-GCM（带认证标签，防篡改）。
    /// </summary>
    internal static class AccountPortableExport
    {
        private const int Version = 1;
        private const int SaltBytes = 16;
        private const int NonceBytes = 12;
        private const int TagBytes = 16;
        private const int Iterations = 200_000;

        /// <summary>导出文件的外层结构</summary>
        private sealed class Envelope
        {
            public int Version { get; set; } = AccountPortableExport.Version;
            public string Salt { get; set; } = "";
            public string Nonce { get; set; } = "";
            public string Tag { get; set; } = "";
            public string Payload { get; set; } = "";
            public string CreatedAtUtc { get; set; } = "";
            public int AccountCount { get; set; }
        }

        /// <summary>导出的单条账号（密码为明文，仅存在于加密载荷内部）</summary>
        private sealed class ExportedAccount
        {
            public string DisplayName { get; set; } = "";
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
            public string Tags { get; set; } = "";
            public bool IsPlaceholder { get; set; }
        }

        /// <summary>把账号导出为加密文件。口令由用户自己设定，不落盘。</summary>
        public static void Export(string path, IEnumerable<SeewoAccount> accounts, string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                throw new ArgumentException("导出密码至少 6 位", nameof(password));

            var list = accounts.Select(a => new ExportedAccount
            {
                DisplayName = a.DisplayName ?? "",
                Username = a.Username ?? "",
                Password = a.DecryptedPassword ?? "",
                Tags = a.Tags != null && a.Tags.Count > 0 ? string.Join(",", a.Tags) : "",
                IsPlaceholder = a.IsPlaceholder,
            }).ToList();

            var plain = JsonSerializer.SerializeToUtf8Bytes(list);
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var key = DeriveKey(password, salt);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagBytes];

            using (var gcm = new AesGcm(key, TagBytes))
                gcm.Encrypt(nonce, plain, cipher, tag);

            var envelope = new Envelope
            {
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Payload = Convert.ToBase64String(cipher),
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("u"),
                AccountCount = list.Count,
            };

            File.WriteAllText(path, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// 从加密文件导入账号。返回 (账号列表, 错误说明)。
        /// 口令错误或文件被改动时，AES-GCM 的认证会失败 —— 抛出的异常会被转成可读提示。
        /// </summary>
        public static (List<SeewoAccount> Accounts, string Error) Import(string path, string password)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path));
                if (envelope == null || envelope.Version != Version)
                    return (new List<SeewoAccount>(), "文件格式不受支持（可能来自更高版本）");

                var salt = Convert.FromBase64String(envelope.Salt);
                var nonce = Convert.FromBase64String(envelope.Nonce);
                var tag = Convert.FromBase64String(envelope.Tag);
                var cipher = Convert.FromBase64String(envelope.Payload);
                var key = DeriveKey(password, salt);
                var plain = new byte[cipher.Length];

                using (var gcm = new AesGcm(key, TagBytes))
                    gcm.Decrypt(nonce, cipher, tag, plain);

                var exported = JsonSerializer.Deserialize<List<ExportedAccount>>(plain) ?? new List<ExportedAccount>();
                var accounts = exported.Select(e => new SeewoAccount
                {
                    DisplayName = e.DisplayName,
                    Username = e.Username,
                    // 导入端重新用本机 DPAPI 加密，密码不以明文落盘
                    Password = string.IsNullOrEmpty(e.Password) ? "" : SecureStore.Encrypt(e.Password),
                    Tags = string.IsNullOrWhiteSpace(e.Tags)
                        ? new List<string>()
                        : e.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList(),
                    IsPlaceholder = e.IsPlaceholder,
                }).ToList();

                return (accounts, "");
            }
            catch (CryptographicException)
            {
                return (new List<SeewoAccount>(), "密码不正确，或文件已损坏");
            }
            catch (Exception ex)
            {
                return (new List<SeewoAccount>(), ex.Message);
            }
        }

        private static byte[] DeriveKey(string password, byte[] salt)
            => Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
    }
}