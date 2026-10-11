namespace Em.Api.Core.ObjectStorage;

/// <summary>
/// In-process lock per blob hash, held while a blob's files and its database rows change together: upload
/// (from the existence check until the version is committed), replication, repair and garbage collection.
/// Assumes one API instance per object storage database and volume set.
/// </summary>
public sealed class StoreHashLock
{
   private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

   private sealed class Entry
   {
      public readonly SemaphoreSlim Gate = new(1, 1);
      public int Users;
   }

   /// <summary>Waits for the lock of <paramref name="hash"/>; dispose the result to release it.</summary>
   public async Task<IDisposable> EnterAsync(byte[] hash, CancellationToken ct) {
      var key = Convert.ToHexString(hash);
      Entry entry;
      lock (_entries) {
         if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
         entry.Users++;
      }
      try {
         await entry.Gate.WaitAsync(ct);
      }
      catch {
         Leave(key, entry, false);
         throw;
      }
      return new Releaser(this, key, entry);
   }

   /// <summary>Number of hashes currently locked or waited for; for diagnostics and tests.</summary>
   public int ActiveCount {
      get { lock (_entries) return _entries.Count; }
   }

   private void Leave(string key, Entry entry, bool release) {
      if (release) entry.Gate.Release();
      lock (_entries) {
         if (--entry.Users == 0) _entries.Remove(key);
      }
   }

   private sealed class Releaser(StoreHashLock owner, string key, Entry entry) : IDisposable
   {
      private int _released;

      public void Dispose() {
         if (Interlocked.Exchange(ref _released, 1) == 0) owner.Leave(key, entry, true);
      }
   }
}
