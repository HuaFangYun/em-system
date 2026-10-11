using System.IO;
using Em.Api.Core.Models;
using Em.Shared;
using Em.Ui.Wpf.Core;

namespace Em.Api.Core;

/// <summary>Client-side implementation of the object storage management actions.</summary>
[Module(Defaults.AdministrativeToolsModuleName)]
public sealed class ObjectStorageService(EmApp app) : ServiceWpfBase(app), IObjectStorageServices
{
   /// <inheritdoc />
   public Task<StoreStatus> GetMeta_StoreStatus() => GetAsync<StoreStatus>(nameof(GetMeta_StoreStatus));
   /// <inheritdoc />
   public Task<StoreVolumeInfo[]> GetMeta_StoreVolumes() => GetAsync<StoreVolumeInfo[]>(nameof(GetMeta_StoreVolumes));
   /// <inheritdoc />
   public Task<StoreVolumeValidation> PostGetMeta_StoreVolumeValidate(StoreVolumeSave draft) => PostAsync<StoreVolumeValidation>(nameof(PostGetMeta_StoreVolumeValidate), draft);
   /// <inheritdoc />
   public Task<StoreVolumeInfo> PostGetMeta_StoreVolumeSave(StoreVolumeSave request) => PostAsync<StoreVolumeInfo>(nameof(PostGetMeta_StoreVolumeSave), request);
   /// <inheritdoc />
   public Task PostMeta_StoreVolumeDelete(string volumeId) => PostAsync(nameof(PostMeta_StoreVolumeDelete), volumeId);
   /// <inheritdoc />
   public Task<StorePolicyInfo[]> GetMeta_StorePolicies() => GetAsync<StorePolicyInfo[]>(nameof(GetMeta_StorePolicies));
   /// <inheritdoc />
   public Task<StorePolicyInfo> PostGetMeta_StorePolicySave(StorePolicySave request) => PostAsync<StorePolicyInfo>(nameof(PostGetMeta_StorePolicySave), request);
   /// <inheritdoc />
   public Task PostMeta_StorePolicyDelete(string policyId) => PostAsync(nameof(PostMeta_StorePolicyDelete), policyId);
   /// <inheritdoc />
   public Task<StoreBucketInfo[]> GetMeta_StoreBuckets() => GetAsync<StoreBucketInfo[]>(nameof(GetMeta_StoreBuckets));
   /// <inheritdoc />
   public Task<StoreBucketInfo> PostGetMeta_StoreBucketSave(StoreBucketSave request) => PostAsync<StoreBucketInfo>(nameof(PostGetMeta_StoreBucketSave), request);
   /// <inheritdoc />
   public Task PostMeta_StoreBucketDelete(string bucketId) => PostAsync(nameof(PostMeta_StoreBucketDelete), bucketId);
   /// <inheritdoc />
   public Task<StoreObjectListing> GetMeta_StoreObjects(string bucketId, string? prefix, string? delimiter, string? continuation, int limit) =>
      GetAsync<StoreObjectListing>(nameof(GetMeta_StoreObjects), bucketId, prefix!, delimiter!, continuation!, limit);
   /// <inheritdoc />
   public Task<StoreObjectInfo> GetMeta_StoreObject(string bucketId, string key) => GetAsync<StoreObjectInfo>(nameof(GetMeta_StoreObject), bucketId, key);
   /// <inheritdoc />
   public Task<StoreObjectInfo> PostGetMeta_StoreObjectUpload(StoreUploadRequest request, Stream content) =>
      PostStreamAsync<StoreObjectInfo>(nameof(PostGetMeta_StoreObjectUpload), content, request);
   /// <inheritdoc />
   public Task<Stream> GetMeta_StoreObjectDownload(string bucketId, string key, int? version) =>
      GetStreamAsync(nameof(GetMeta_StoreObjectDownload), bucketId, key, version!);
   /// <inheritdoc />
   public Task PostMeta_StoreObjectDelete(string bucketId, string key) => PostAsync(nameof(PostMeta_StoreObjectDelete), bucketId, key);
   /// <inheritdoc />
   public Task<StoreObjectVersionInfo[]> GetMeta_StoreObjectVersions(string bucketId, string key) =>
      GetAsync<StoreObjectVersionInfo[]>(nameof(GetMeta_StoreObjectVersions), bucketId, key);
   /// <inheritdoc />
   public Task<StoreObjectInfo> PostGetMeta_StoreObjectRestore(string bucketId, string key, int version) =>
      PostAsync<StoreObjectInfo>(nameof(PostGetMeta_StoreObjectRestore), bucketId, key, version);
   /// <inheritdoc />
   public Task PostMeta_StoreObjectVersionPurge(string bucketId, string key, int version) =>
      PostAsync(nameof(PostMeta_StoreObjectVersionPurge), bucketId, key, version);
   /// <inheritdoc />
   public Task<StoreGcReview> GetMeta_StoreGcReview(int graceHours) => GetAsync<StoreGcReview>(nameof(GetMeta_StoreGcReview), graceHours);
   /// <inheritdoc />
   public Task<BusinessTaskInfo> PostGetMeta_StoreGcRun(int graceHours) => PostAsync<BusinessTaskInfo>(nameof(PostGetMeta_StoreGcRun), graceHours);
   /// <inheritdoc />
   public Task<BusinessTaskInfo> PostGetMeta_StoreRetentionRun() => PostAsync<BusinessTaskInfo>(nameof(PostGetMeta_StoreRetentionRun));
   /// <inheritdoc />
   public Task<BusinessTaskInfo> PostGetMeta_StoreRepairRun() => PostAsync<BusinessTaskInfo>(nameof(PostGetMeta_StoreRepairRun));
   /// <inheritdoc />
   public Task<BusinessTaskInfo[]> GetMeta_StoreTasks() => GetAsync<BusinessTaskInfo[]>(nameof(GetMeta_StoreTasks));
}
