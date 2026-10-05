using System;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// .NET Framework 4.8 没有 System.Security.Cryptography.AesGcm，这里用 BouncyCastle 的
    /// GCM 实现补齐，API 形状与 BCL 的 AesGcm.Encrypt/Decrypt 一致（密文与 16 字节 tag 分开传递），
    /// 以便 v2 凭据格式与可迁移导出文件在 Win10 分支和 Win7 分支之间互相解开。
    ///
    /// 注意：GcmBlockCipher 的 ProcessBytes 产生密文、DoFinal 追加 tag，
    /// 因此这里显式按 ciphertext || tag 的顺序切分，与 BCL 的 AesGcm 输出等价。
    /// </summary>
    internal static class AesGcmCompat
    {
        private const int TagSize = 16;

        public static void Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag)
        {
            var sealedBytes = Process(true, key, nonce, plaintext, plaintext.Length);
            Buffer.BlockCopy(sealedBytes, 0, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(sealedBytes, ciphertext.Length, tag, 0, tag.Length);
        }

        public static void Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext)
        {
            var sealedBytes = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, sealedBytes, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, sealedBytes, ciphertext.Length, tag.Length);

            var result = Process(false, key, nonce, sealedBytes, ciphertext.Length);
            Buffer.BlockCopy(result, 0, plaintext, 0, plaintext.Length);
        }

        private static byte[] Process(bool forEncryption, byte[] key, byte[] nonce, byte[] input, int plainLength)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(forEncryption, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce));

            var output = new byte[cipher.GetOutputSize(forEncryption ? plainLength : input.Length)];
            var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            len += cipher.DoFinal(output, len);

            if (len != output.Length)
            {
                // DoFinal 抛 CryptographicException 表示 tag 校验失败；走到这里说明长度异常，属于实现错误。
                var trimmed = new byte[len];
                Buffer.BlockCopy(output, 0, trimmed, 0, len);
                return trimmed;
            }
            return output;
        }
    }
}
