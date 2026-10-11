using Em.Api.Core.Storage;
using Em.Shared;

namespace Em.Api.Core.ObjectStorage;

/// <summary>
/// <see cref="IBinaryStorage"/> on top of one object storage bucket, for modules that want binary storage
/// with the object storage policies (replicas, quota) behind it. Each <see cref="IBinaryStorage"/> key maps
/// to the object key of the same text. It is not registered by the engine and does not replace the binary
/// storage the approval engine uses; create it from a scoped <see cref="IObjectStore"/>.
/// </summary>
/// <remarks>
/// <see cref="PutAsync"/> keeps the no-overwrite rule of <see cref="IBinaryStorage"/>: a key that already has
/// any version, including a deleted one, is refused with 409. The check and the write are not one
/// transaction, so two simultaneous puts of the same new key may both succeed as two versions.
/// <see cref="DeleteAsync"/> purges every version, so the content is really gone afterwards.
/// </remarks>
/// <param name="store">Object store of the current scope.</param>
/// <param name="bucket">Name of an existing bucket.</param>
public sealed class ObjectStoreBinaryStorage(IObjectStore store, string bucket) : IBinaryStorage
{
   /// <inheritdoc />
   public async Task<string> PutAsync(string key, Stream content, CancellationToken cancellationToken = default) {
      key = BinaryStorageKey.Normalize(key);
      if (await store.GetAsync(bucket, key, cancellationToken) is not null)
         throw new ActionException($"Storage key '{key}' is already used.", 409);
      await store.PutAsync(bucket, key, content, null, null, cancellationToken);
      return key;
   }

   /// <inheritdoc />
   public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
      (await store.OpenAsync(bucket, BinaryStorageKey.Normalize(key), null, cancellationToken)).Content;

   /// <inheritdoc />
   public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) {
      key = BinaryStorageKey.Normalize(key);
      if (await store.GetAsync(bucket, key, cancellationToken) is null) return;
      foreach (var version in await store.VersionsAsync(bucket, key, cancellationToken)) {
         try { await store.PurgeVersionAsync(bucket, key, version.Version, cancellationToken); }
         catch (ActionException ex) when (ex.StatusCode == 404) { }
      }
   }

   /// <inheritdoc />
   public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
      await store.GetAsync(bucket, BinaryStorageKey.Normalize(key), cancellationToken) is { IsDeleteMarker: false };
}
