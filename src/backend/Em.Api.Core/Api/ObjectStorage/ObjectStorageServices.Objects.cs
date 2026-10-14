using Em.Api.Core.Models;
using Em.Shared;

namespace Em.Api.Core.ObjectStorage;

public sealed partial class ObjectStorageServices
{
   private async Task<string> BucketName(string bucketId) => (await store.RequireBucketByIdAsync(bucketId, AbortToken)).cStoreBucketName;

   /// <summary>
   /// Name of a bucket whose contents may change here; 409 for a managed bucket, whose contents belong to its
   /// application (it still writes through <see cref="IObjectStore"/>).
   /// </summary>
   private async Task<string> WritableBucketName(string bucketId) {
      var bucket = await store.RequireBucketByIdAsync(bucketId, AbortToken);
      if (ManagedBucketJson.Owner(bucket.json_object) is { } owner)
         throw ManagedConflict(bucket.cStoreBucketName, owner, "its contents can only be changed by that application. They can still be browsed and downloaded here.");
      return bucket.cStoreBucketName;
   }

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectListing> GetMeta_StoreObjects(string bucketId, string? prefix, string? delimiter, string? continuation, int limit) =>
      await store.ListAsync(await BucketName(bucketId), prefix, delimiter, continuation, limit, AbortToken);

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectInfo> GetMeta_StoreObject(string bucketId, string key) =>
      await store.GetAsync(await BucketName(bucketId), key, AbortToken) ?? throw new ActionException("Object not found.", 404);

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectInfo> PostGetMeta_StoreObjectUpload(StoreUploadRequest request, Stream content) {
      ArgumentNullException.ThrowIfNull(request);
      // Checked before the content stream is read, so a refused upload does not transfer its body.
      var bucket = await WritableBucketName(request.BucketId);
      return await store.PutAsync(bucket, request.Key, content, request.Mime, request.Metadata, AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<Stream> GetMeta_StoreObjectDownload(string bucketId, string key, int? version) =>
      (await store.OpenAsync(await BucketName(bucketId), key, version, AbortToken)).Content;

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task PostMeta_StoreObjectDelete(string bucketId, string key) {
      var bucket = await WritableBucketName(bucketId);
      await store.DeleteAsync(bucket, key, AbortToken);
      await AuditSystemAsync("System.ObjectStorage", $"{bucket}/{key}", "object.deleted", $"Deleted object {bucket}/{key}", "object");
   }

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectVersionInfo[]> GetMeta_StoreObjectVersions(string bucketId, string key) =>
      await store.VersionsAsync(await BucketName(bucketId), key, AbortToken);

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectInfo> PostGetMeta_StoreObjectRestore(string bucketId, string key, int version) =>
      await store.RestoreAsync(await WritableBucketName(bucketId), key, version, AbortToken);

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.SettingsClaim)]
   public async Task PostMeta_StoreObjectVersionPurge(string bucketId, string key, int version) {
      var bucket = await WritableBucketName(bucketId);
      await store.PurgeVersionAsync(bucket, key, version, AbortToken);
      await AuditSystemAsync("System.ObjectStorage", $"{bucket}/{key}", "version.purged", $"Purged version {version} of {bucket}/{key}", "object",
         new { version });
   }
}
