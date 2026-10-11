using Em.Api.Core.Models;

namespace Em.Api.Core.ObjectStorage.Volumes;

/// <summary>
/// Volumes in a folder of the API server's file system: a local disk or a mounted NAS share. Relative paths
/// are resolved against the API content root.
/// </summary>
/// <remarks>
/// Layout: <c>&lt;volume&gt;/blobs/&lt;hex[0..2]&gt;/&lt;hex[2..4]&gt;/&lt;hex&gt;</c> for blob content, read-only
/// after it is written, and <c>&lt;volume&gt;/.staging/&lt;ulid&gt;</c> for writes in progress.
/// </remarks>
public sealed class LocalFolderVolumeProvider(string contentRoot) : IStoreVolumeProvider
{
   /// <inheritdoc />
   public StoreVolumeType Type => StoreVolumeType.LocalFolder;

   /// <summary>Resolves a configured path to an absolute folder, throwing 400 when it cannot be used.</summary>
   public string Resolve(string? path) {
      if (string.IsNullOrWhiteSpace(path)) throw new Em.Shared.ActionException("Volume path is required.", 400);
      path = path.Trim();
      if (path.Length > 400 || path.Any(char.IsControl)) throw new Em.Shared.ActionException("Volume path is invalid.", 400);
      string full;
      try { full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path)); }
      catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
         throw new Em.Shared.ActionException("Volume path is invalid.", 400);
      }
      if (Path.GetPathRoot(full) == full) throw new Em.Shared.ActionException("A drive or file system root cannot be a volume.", 400);
      return full;
   }

   /// <inheritdoc />
   public StoreVolumeValidation Validate(string path) {
      string full;
      try { full = Resolve(path); }
      catch (Em.Shared.ActionException ex) { return new(false, null, ex.Message, null, false); }
      return new LocalFolderVolume("", full).Probe();
   }

   /// <inheritdoc />
   public IStoreVolume Open(string volumeId, string path) => new LocalFolderVolume(volumeId, Resolve(path));
}

/// <summary>A volume in a local or mounted folder.</summary>
internal sealed class LocalFolderVolume(string id, string root) : IStoreVolume
{
   private const int BufferSize = 81920;

   public string Id { get; } = id;
   public string Location => root;
   private string StagingRoot => Path.Combine(root, ".staging");

   internal string BlobPath(byte[] hash) {
      if (hash.Length != 32) throw new ArgumentException("A blob hash has 32 bytes.", nameof(hash));
      var hex = Convert.ToHexStringLower(hash);
      return Path.Combine(root, "blobs", hex[..2], hex[2..4], hex);
   }

   public StoreVolumeValidation Probe() {
      try {
         Directory.CreateDirectory(root);
         Directory.CreateDirectory(StagingRoot);
         var probe = Path.Combine(StagingRoot, $"probe-{Ulid.NewUlid()}");
         File.WriteAllBytes(probe, [1]);
         File.Delete(probe);
         long? free = null;
         try { free = new DriveInfo(root).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
         return new(true, root, "The folder is reachable and writable.", free, true);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
         return new(false, root, $"The folder cannot be used: {ex.Message}", null, false);
      }
   }

   public IStoreStaging BeginWrite() {
      Directory.CreateDirectory(StagingRoot);
      var path = Path.Combine(StagingRoot, Ulid.NewUlid().ToString());
      var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.Asynchronous);
      return new Staging(this, path, stream);
   }

   public bool TryGetLength(byte[] hash, out long length) {
      var info = new FileInfo(BlobPath(hash));
      try {
         if (info.Exists) { length = info.Length; return true; }
      }
      catch (IOException) { }
      length = 0;
      return false;
   }

   public Stream? OpenRead(byte[] hash) {
      try {
         // FileShare.Delete: garbage collection never deletes a referenced blob, but repair may replace a
         // damaged copy while a download is still reading the old handle.
         return new FileStream(BlobPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
      }
      catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
         return null;
      }
   }

   public bool Delete(byte[] hash) {
      var path = BlobPath(hash);
      if (!File.Exists(path)) return false;
      File.SetAttributes(path, FileAttributes.Normal);
      File.Delete(path);
      // Leave the two-level fan-out folders in place: they are shared by many blobs and cheap to keep.
      return true;
   }

   public int CleanStaging() {
      if (!Directory.Exists(StagingRoot)) return 0;
      var count = 0;
      foreach (var entry in Directory.EnumerateFileSystemEntries(StagingRoot)) {
         try {
            if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            count++;
         }
         catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
      }
      return count;
   }

   private sealed class Staging(LocalFolderVolume volume, string path, FileStream stream) : IStoreStaging
   {
      private bool _committed;
      public Stream Stream => stream;

      public Stream OpenRead() {
         stream.Flush();
         return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
      }

      public async Task CommitAsync(byte[] hash, long expectedLength, CancellationToken ct) {
         await stream.FlushAsync(ct);
         stream.Flush(true);
         await stream.DisposeAsync();
         if (new FileInfo(path).Length != expectedLength) throw new IOException("Staged content has an unexpected length.");
         var target = volume.BlobPath(hash);
         Directory.CreateDirectory(Path.GetDirectoryName(target)!);
         if (File.Exists(target) && new FileInfo(target).Length == expectedLength) {
            // The same content is already in place (a copy left by an interrupted write). Content is
            // identified by its hash, so the existing file is kept and the staged one discarded.
            File.Delete(path);
         }
         else {
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Move(path, target, true);
            File.SetAttributes(target, FileAttributes.ReadOnly);
         }
         _committed = true;
      }

      public async ValueTask DisposeAsync() {
         await stream.DisposeAsync();
         if (_committed) return;
         try { File.Delete(path); }
         catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
      }
   }
}
