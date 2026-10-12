namespace Em.Api.Core.Log;

/// <summary>
/// Stops "log about log": while the writer, ingest or shipper run, log lines they cause (EF Core commands, store
/// errors) are not queued again. Flows with the async context.
/// </summary>
internal static class LogGuard
{
   private static readonly AsyncLocal<bool> Suppressed = new();

   public static bool IsSuppressed => Suppressed.Value;

   /// <summary>Suppresses queueing until the returned scope is disposed.</summary>
   public static IDisposable Suppress() {
      var previous = Suppressed.Value;
      Suppressed.Value = true;
      return new Restore(previous);
   }

   private sealed class Restore(bool previous) : IDisposable
   {
      public void Dispose() => Suppressed.Value = previous;
   }
}
