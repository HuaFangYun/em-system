using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Em.Shared;

namespace Em.Api.Core.ObjectStorage;

/// <summary>
/// Validation and normalization of the names object storage accepts: bucket names, object keys, MIME types
/// and user metadata. Every failure is an <see cref="ActionException"/> with status 400.
/// </summary>
public static partial class StoreNames
{
   /// <summary>Maximum length of an object key in UTF-8 bytes.</summary>
   public const int MaxKeyBytes = 1024;

   /// <summary>Maximum number of user metadata entries per version.</summary>
   public const int MaxMetadataEntries = 32;

   /// <summary>MIME type stored when an upload does not name one.</summary>
   public const string DefaultMime = "application/octet-stream";

   private static readonly UTF8Encoding StrictUtf8 = new(false, true);

   [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
   private static partial Regex BucketPattern();

   [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
   private static partial Regex IpPattern();

   [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}( *;[ -~]*)?$", RegexOptions.CultureInvariant)]
   private static partial Regex MimePattern();

   [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
   private static partial Regex MetadataKeyPattern();

   /// <summary>
   /// Checks a bucket name against the S3 naming rules: 3–63 characters of lowercase letters, digits,
   /// <c>-</c> and <c>.</c>, starting and ending with a letter or digit, without <c>..</c>, not shaped like
   /// an IP address, and without the reserved <c>xn--</c> prefix or <c>-s3alias</c> suffix.
   /// </summary>
   /// <returns>The name unchanged.</returns>
   public static string Bucket(string? name) {
      if (string.IsNullOrEmpty(name) || !BucketPattern().IsMatch(name) || name.Contains("..") || name.Contains(".-") ||
          name.Contains("-.") || IpPattern().IsMatch(name) || name.StartsWith("xn--", StringComparison.Ordinal) ||
          name.EndsWith("-s3alias", StringComparison.Ordinal))
         throw new ActionException("Invalid bucket name. Use 3–63 lowercase letters, digits, '-' and '.', starting and ending with a letter or digit.", 400);
      return name;
   }

   /// <summary>
   /// Checks an object key: 1–1024 bytes of valid UTF-8, no control characters, and no <c>.</c> or <c>..</c>
   /// segment between <c>/</c> separators. The key is case-sensitive and is never used as a file path.
   /// </summary>
   /// <returns>The key unchanged.</returns>
   public static string Key(string? key) {
      if (string.IsNullOrEmpty(key)) throw new ActionException("Object key is required.", 400);
      int bytes;
      try { bytes = StrictUtf8.GetByteCount(key); }
      catch (EncoderFallbackException) { throw new ActionException("Object key is not valid Unicode text.", 400); }
      if (bytes > MaxKeyBytes) throw new ActionException($"Object key exceeds {MaxKeyBytes} UTF-8 bytes.", 400);
      if (key.Any(char.IsControl)) throw new ActionException("Object key must not contain control characters.", 400);
      if (key.Split('/').Any(s => s is "." or ".."))
         throw new ActionException("Object key must not contain '.' or '..' segments.", 400);
      return key;
   }

   /// <summary>Checks an optional key prefix used for listing; empty means no prefix.</summary>
   public static string Prefix(string? prefix) {
      if (string.IsNullOrEmpty(prefix)) return "";
      try { if (StrictUtf8.GetByteCount(prefix) > MaxKeyBytes) throw new ActionException("Prefix is too long.", 400); }
      catch (EncoderFallbackException) { throw new ActionException("Prefix is not valid Unicode text.", 400); }
      if (prefix.Any(char.IsControl)) throw new ActionException("Prefix must not contain control characters.", 400);
      return prefix;
   }

   /// <summary>SHA-256 of the UTF-8 bytes of a key, the identity of the key inside its bucket.</summary>
   public static byte[] KeyHash(string key) => SHA256.HashData(StrictUtf8.GetBytes(key));

   /// <summary>Lowercase hex of a hash.</summary>
   public static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

   /// <summary>Parses a 64-character SHA-256 hex string, throwing 400 when it is not one.</summary>
   public static byte[] ParseHash(string? hex) {
      if (hex is not { Length: 64 }) throw new ActionException("Invalid blob hash.", 400);
      try { return Convert.FromHexString(hex); }
      catch (FormatException) { throw new ActionException("Invalid blob hash.", 400); }
   }

   /// <summary>Checks a MIME type; <c>null</c> or empty gives <see cref="DefaultMime"/>.</summary>
   public static string Mime(string? mime) {
      if (string.IsNullOrWhiteSpace(mime)) return DefaultMime;
      mime = mime.Trim();
      if (mime.Length > 255 || !MimePattern().IsMatch(mime)) throw new ActionException("Invalid MIME type.", 400);
      return mime;
   }

   /// <summary>
   /// Checks user metadata (at most 32 entries, keys of letters, digits, <c>.</c>, <c>_</c> and <c>-</c> up to
   /// 128 characters, values up to 1024 characters without control characters) and serializes it as a JSON
   /// object; <c>null</c> when there is none.
   /// </summary>
   public static string? Metadata(IReadOnlyDictionary<string, string>? metadata) {
      if (metadata is null || metadata.Count == 0) return null;
      if (metadata.Count > MaxMetadataEntries) throw new ActionException($"At most {MaxMetadataEntries} metadata entries are allowed.", 400);
      foreach (var (k, v) in metadata) {
         if (k is null || !MetadataKeyPattern().IsMatch(k)) throw new ActionException($"Invalid metadata key '{k}'.", 400);
         if (v is null || v.Length > 1024 || v.Any(c => char.IsControl(c) && c != '\t'))
            throw new ActionException($"Invalid value of metadata key '{k}'.", 400);
      }
      return JsonSerializer.Serialize(metadata.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
   }

   /// <summary>Reads metadata written by <see cref="Metadata"/>; unreadable JSON gives <c>null</c>.</summary>
   public static Dictionary<string, string>? ReadMetadata(string? json) {
      if (string.IsNullOrWhiteSpace(json)) return null;
      try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
      catch (JsonException) { return null; }
   }

   /// <summary>Checks a display name of a volume or policy: 1–100 characters after trimming.</summary>
   public static string DisplayName(string? name, string what) {
      name = name?.Trim();
      if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl))
         throw new ActionException($"{what} name must contain 1–100 characters.", 400);
      return name;
   }

   /// <summary>Trims an optional note; empty gives <c>null</c>, more than 500 characters is rejected.</summary>
   public static string? Note(string? note) {
      note = note?.Trim();
      if (string.IsNullOrEmpty(note)) return null;
      if (note.Length > 500) throw new ActionException("Note must not exceed 500 characters.", 400);
      return note;
   }

   /// <summary>Encodes a listing position as an opaque continuation token.</summary>
   public static string EncodeContinuation(string after) =>
      Convert.ToBase64String(Encoding.UTF8.GetBytes("k1:" + after)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

   /// <summary>Decodes a continuation token; <c>null</c> or empty gives <c>null</c>, a foreign token is rejected with 400.</summary>
   public static string? DecodeContinuation(string? token) {
      if (string.IsNullOrEmpty(token)) return null;
      try {
         var base64 = token.Replace('-', '+').Replace('_', '/');
         base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
         var text = StrictUtf8.GetString(Convert.FromBase64String(base64));
         if (text.StartsWith("k1:", StringComparison.Ordinal)) return text[3..];
      }
      catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { }
      throw new ActionException("Invalid continuation token.", 400);
   }
}
