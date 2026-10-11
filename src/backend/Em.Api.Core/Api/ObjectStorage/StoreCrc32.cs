namespace Em.Api.Core.ObjectStorage;

/// <summary>
/// Incremental CRC-32 (IEEE 802.3, the polynomial used by zip and gzip). Used as a quick check that a copy
/// of a blob still matches; the identity of a blob is its SHA-256.
/// </summary>
public sealed class StoreCrc32
{
   private static readonly uint[] Table = CreateTable();
   private uint _crc = 0xFFFFFFFFu;

   private static uint[] CreateTable() {
      var table = new uint[256];
      for (uint i = 0; i < 256; i++) {
         var c = i;
         for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
         table[i] = c;
      }
      return table;
   }

   /// <summary>Adds bytes to the checksum.</summary>
   public void Append(ReadOnlySpan<byte> data) {
      var crc = _crc;
      foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
      _crc = crc;
   }

   /// <summary>Current checksum value.</summary>
   public uint Value => _crc ^ 0xFFFFFFFFu;

   /// <summary>Current checksum as four big-endian bytes, the form stored in the database.</summary>
   public byte[] ToBytes() {
      var v = Value;
      return [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
   }

   /// <summary>Checksum of a whole buffer as four big-endian bytes.</summary>
   public static byte[] Compute(ReadOnlySpan<byte> data) {
      var crc = new StoreCrc32();
      crc.Append(data);
      return crc.ToBytes();
   }
}
