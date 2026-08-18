using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeewoAutoLogin
{
    public sealed class QrSessionStore
    {
        private const int CryptProtectUiForbidden = 0x1;
        private static readonly byte[] EntropyPrefix = Encoding.UTF8.GetBytes("com.icc.seewo-autologin/qr-session/v1/");
        private readonly string _sessionsDirectory;

        public QrSessionStore()
        {
            _sessionsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeewoAutoLogin",
                "Sessions");
        }

        public string CreateCredentialId() => Guid.NewGuid().ToString("N");

        public void Save(string credentialId, string token, DateTimeOffset acquiredAt)
        {
            ValidateCredentialId(credentialId);
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("会话令牌不能为空。", nameof(token));

            var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredQrSession
            {
                Version = 1,
                Token = token,
                AcquiredAtUtc = acquiredAt.ToUniversalTime()
            });
            var protectedPayload = Protect(payload, BuildEntropy(credentialId));
            Array.Clear(payload, 0, payload.Length);

            Directory.CreateDirectory(_sessionsDirectory);
            var path = GetPath(credentialId);
            var temporaryPath = path + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, protectedPayload);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                Array.Clear(protectedPayload, 0, protectedPayload.Length);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public bool TryLoad(string credentialId, out QrSessionSecret session)
        {
            session = null;
            try
            {
                ValidateCredentialId(credentialId);
                var path = GetPath(credentialId);
                if (!File.Exists(path)) return false;

                var protectedPayload = File.ReadAllBytes(path);
                byte[] payload = null;
                try
                {
                    payload = Unprotect(protectedPayload, BuildEntropy(credentialId));
                    var stored = JsonSerializer.Deserialize<StoredQrSession>(payload);
                    if (stored?.Version != 1 || string.IsNullOrWhiteSpace(stored.Token)) return false;
                    session = new QrSessionSecret(stored.Token, stored.AcquiredAtUtc);
                    return true;
                }
                finally
                {
                    Array.Clear(protectedPayload, 0, protectedPayload.Length);
                    if (payload != null) Array.Clear(payload, 0, payload.Length);
                }
            }
            catch (CryptographicException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (JsonException) { return false; }
        }

        public void Delete(string credentialId)
        {
            if (string.IsNullOrWhiteSpace(credentialId)) return;
            ValidateCredentialId(credentialId);
            var path = GetPath(credentialId);
            if (File.Exists(path)) File.Delete(path);
        }

        private string GetPath(string credentialId) => Path.Combine(_sessionsDirectory, credentialId + ".bin");

        private static void ValidateCredentialId(string credentialId)
        {
            if (string.IsNullOrWhiteSpace(credentialId) || credentialId.Length != 32)
                throw new ArgumentException("凭据引用无效。", nameof(credentialId));
            foreach (var character in credentialId)
            {
                if (!Uri.IsHexDigit(character))
                    throw new ArgumentException("凭据引用无效。", nameof(credentialId));
            }
        }

        private static byte[] BuildEntropy(string credentialId)
        {
            var suffix = Encoding.UTF8.GetBytes(credentialId);
            var entropy = new byte[EntropyPrefix.Length + suffix.Length];
            Buffer.BlockCopy(EntropyPrefix, 0, entropy, 0, EntropyPrefix.Length);
            Buffer.BlockCopy(suffix, 0, entropy, EntropyPrefix.Length, suffix.Length);
            return entropy;
        }

        private static byte[] Protect(byte[] data, byte[] entropy)
        {
            return Transform(data, entropy, true);
        }

        private static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            return Transform(data, entropy, false);
        }

        private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
        {
            var input = CreateBlob(data);
            var optionalEntropy = CreateBlob(entropy);
            DataBlob output = default;
            try
            {
                var succeeded = protect
                    ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
                if (!succeeded)
                    throw new CryptographicException(Marshal.GetLastWin32Error());

                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                FreeBlob(ref input, true);
                FreeBlob(ref optionalEntropy, true);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }

        private static DataBlob CreateBlob(byte[] data)
        {
            if (data == null || data.Length == 0) return default;
            var pointer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pointer, data.Length);
            return new DataBlob { Length = data.Length, Data = pointer };
        }

        private static void FreeBlob(ref DataBlob blob, bool clear)
        {
            if (blob.Data == IntPtr.Zero) return;
            if (clear && blob.Length > 0)
            {
                var zeros = new byte[blob.Length];
                Marshal.Copy(zeros, 0, blob.Data, blob.Length);
            }
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        private sealed class StoredQrSession
        {
            public int Version { get; set; }
            public string Token { get; set; } = "";
            public DateTimeOffset AcquiredAtUtc { get; set; }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn, string description, ref DataBlob optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }

    public sealed class QrSessionSecret
    {
        public QrSessionSecret(string token, DateTimeOffset acquiredAtUtc)
        {
            Token = token;
            AcquiredAtUtc = acquiredAtUtc;
        }
        public string Token { get; }
        public DateTimeOffset AcquiredAtUtc { get; }
    }
}
