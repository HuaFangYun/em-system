using System.Security.Cryptography;
using System.Text;

namespace Em.Api.Core.Log;

/// <summary>AES-GCM encryption of stored store passwords; the key lives in <c>ta_Meta</c> under its own row.</summary>
internal static class LogSecrets
{
   // Version(1), nonce(12), tag(16), ciphertext.
   internal static byte[] Encrypt(byte[] key, string password) {
      var plain = Encoding.UTF8.GetBytes(password);
      try {
         var data = new byte[29 + plain.Length];
         data[0] = 1;
         RandomNumberGenerator.Fill(data.AsSpan(1, 12));
         using var aes = new AesGcm(key, 16);
         aes.Encrypt(data.AsSpan(1, 12), plain, data.AsSpan(29), data.AsSpan(13, 16));
         return data;
      }
      finally { CryptographicOperations.ZeroMemory(plain); }
   }

   internal static string Decrypt(byte[] key, byte[] data) {
      if (data.Length < 29 || data[0] != 1) throw new CryptographicException("Invalid log store secret format.");
      var plain = new byte[data.Length - 29];
      try {
         using var aes = new AesGcm(key, 16);
         aes.Decrypt(data.AsSpan(1, 12), data.AsSpan(29), data.AsSpan(13, 16), plain);
         return Encoding.UTF8.GetString(plain);
      }
      finally { CryptographicOperations.ZeroMemory(plain); }
   }
}
