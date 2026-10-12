using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Em.Api.Core.Log;

/// <summary>
/// Groups errors: the exception type plus the top stack frame that is not a framework frame, hashed to 32 hex
/// characters. Line numbers, file paths and generic arity are left out so a rebuild or a moved line keeps the
/// same group.
/// </summary>
internal static partial class ErrorFingerprint
{
   private static readonly string[] FrameworkPrefixes = [
      "System.", "Microsoft.", "Npgsql.", "MySqlConnector.", "Pomelo.", "EFCore.", "Em.Api.Core.EmApp", "Em.Ui.Wpf.Core.EmApp",
      "MS.", "lambda_method"
   ];

   /// <summary>Exception type name of an exception text (first line up to the first colon).</summary>
   public static string TypeOf(string exceptionText) {
      var first = exceptionText.AsSpan();
      var newline = first.IndexOfAny('\r', '\n');
      if (newline >= 0) first = first[..newline];
      var colon = first.IndexOf(':');
      var type = (colon >= 0 ? first[..colon] : first).Trim().ToString();
      return type.Length > 300 ? type[..300] : type;
   }

   /// <summary>The top application frame (method name only), or the top frame when every frame is framework code.</summary>
   public static string? TopFrameOf(string exceptionText) {
      string? first = null;
      foreach (var raw in exceptionText.Split('\n')) {
         var line = raw.Trim();
         if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
         var frame = CleanFrame(line[3..]);
         first ??= frame;
         if (!FrameworkPrefixes.Any(p => frame.StartsWith(p, StringComparison.Ordinal))) return frame;
      }
      return first;
   }

   /// <summary>Fingerprint of an exception text.</summary>
   public static string Compute(string exceptionText) {
      var key = TypeOf(exceptionText) + "|" + (TopFrameOf(exceptionText) ?? "");
      var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
      return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
   }

   private static string CleanFrame(string frame) {
      var inIndex = frame.IndexOf(" in ", StringComparison.Ordinal);
      if (inIndex >= 0) frame = frame[..inIndex];
      frame = OffsetPattern().Replace(frame, "");
      return frame.Length > 1000 ? frame[..1000] : frame.Trim();
   }

   [GeneratedRegex(@"\s*\+\s*0x[0-9a-fA-F]+")]
   private static partial Regex OffsetPattern();
}
