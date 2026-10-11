using Em.Api.Core.Models;
using Em.Shared;

namespace Em.Api.Core.ObjectStorage;

public sealed partial class ObjectStorageServices
{
   private async Task<string> BucketName(string bucketId) => (await store.RequireBucketByIdAsync(bucketId, AbortToken)).cStoreBucketName;

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
      return await store.PutAsync(await BucketName(request.BucketId), request.Key, content, request.Mime, request.Metadata, AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<Stream> GetMeta_StoreObjectDownload(string bucketId, string key, int? version) =>
      (await store.OpenAsync(await BucketName(bucketId), key, version, AbortToken)).Content;

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task PostMeta_StoreObjectDelete(string bucketId, string key) =>
      await store.DeleteAsync(await BucketName(bucketId), key, AbortToken);

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectVersionInfo[]> GetMeta_StoreObjectVersions(string bucketId, string key) =>
      await store.VersionsAsync(await BucketName(bucketId), key, AbortToken);

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreObjectInfo> PostGetMeta_StoreObjectRestore(string bucketId, string key, int version) =>
      await store.RestoreAsync(await BucketName(bucketId), key, version, AbortToken);

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.SettingsClaim)]
   public async Task PostMeta_StoreObjectVersionPurge(string bucketId, string key, int version) =>
      await store.PurgeVersionAsync(await BucketName(bucketId), key, version, AbortToken);
}
