using Em.Api.Core.Models;

namespace Em.Api.Core.ObjectStorage.Volumes;

/// <summary>
/// Creates <see cref="IStoreVolume"/> instances for one kind of storage. Version 1 only has
/// <see cref="LocalFolderVolumeProvider"/>; remote providers are added later as further implementations.
/// </summary>
public interface IStoreVolumeProvider
{
   /// <summary>Kind of storage this provider handles.</summary>
   StoreVolumeType Type { get; }

   /// <summary>Checks a configured location without saving it.</summary>
   StoreVolumeValidation Validate(string path);

   /// <summary>Opens a volume for its configured location.</summary>
   IStoreVolume Open(string volumeId, string path);
}

/// <summary>
/// One physical location holding blob files named by their hash. Implementations never derive file names
/// from object keys; a blob is written to a staging area and then moved into place atomically.
/// </summary>
public interface IStoreVolume
{
   /// <summary>Volume ID.</summary>
   string Id { get; }

   /// <summary>Location shown to administrators (an absolute folder for local volumes).</summary>
   string Location { get; }

   /// <summary>Checks the location now (reachable, writable, free space).</summary>
   StoreVolumeValidation Probe();

   /// <summary>Starts writing new content to the staging area.</summary>
   IStoreStaging BeginWrite();

   /// <summary>Whether the blob file exists, and its length when it does.</summary>
   bool TryGetLength(byte[] hash, out long length);

   /// <summary>Opens the blob file for reading, or <c>null</c> when it does not exist.</summary>
   Stream? OpenRead(byte[] hash);

   /// <summary>Deletes the blob file; <c>false</c> when it did not exist.</summary>
   bool Delete(byte[] hash);

   /// <summary>Deletes everything left in the staging area; returns the number of entries removed.</summary>
   int CleanStaging();
}

/// <summary>Content being written to a volume's staging area. Disposing without committing deletes it.</summary>
public interface IStoreStaging : IAsyncDisposable
{
   /// <summary>Stream to write the content to.</summary>
   Stream Stream { get; }

   /// <summary>Opens the written content for reading; call after writing has finished.</summary>
   Stream OpenRead();

   /// <summary>Closes the stream and moves the content into place as the blob <paramref name="hash"/>.</summary>
   /// <param name="hash">SHA-256 of the content.</param>
   /// <param name="expectedLength">Length the stored file must have.</param>
   /// <param name="ct">Cancellation.</param>
   Task CommitAsync(byte[] hash, long expectedLength, CancellationToken ct);
}
