using Em.Shared;

namespace Em.Api.Core.Models;

/// <summary>
/// Object storage management: volumes, policies, buckets, objects and their versions, and maintenance
/// (garbage collection, version retention, replication and repair). Reading, uploading, downloading,
/// deleting and restoring objects requires <see cref="ManagerClaim"/>; volumes, policies, creating and
/// deleting buckets, permanent deletion of versions and maintenance require <see cref="SettingsClaim"/>.
/// </summary>
/// <remarks>
/// <para>
/// Server-side modules use <c>IObjectStore</c> in <c>Em.Api.Core</c> directly; these actions are the
/// management surface of the same logic. Objects of a <see cref="StoreAccess.PublicRead"/> bucket are also
/// served without identity at <c>GET /store/&lt;bucket&gt;/&lt;key&gt;</c>.
/// </para>
/// <para>
/// A managed bucket (<see cref="StoreBucketInfo.IsManaged"/>) is declared by an application module and owned by
/// it. Its contents can only be browsed and downloaded here: upload, delete, restore and version purge answer 409.
/// Its policy cannot change, its policy's access cannot change, and neither the bucket nor the policy can be
/// deleted; volumes, replicas, retention, quotas, maximum object size, state and notes stay editable.
/// </para>
/// </remarks>
public interface IObjectStorageServices : IServices
{
   /// <summary>Claim name that opens the Object Storage Manager, written without its module name.</summary>
   const string ManagerClaim = "Object Storage Manager Access";

   /// <summary>Claim name that allows changing volumes, policies, buckets and running maintenance, written without its module name.</summary>
   const string SettingsClaim = "Object Storage Settings Manage";

   /// <summary>Counters, volumes with problems and maintenance tasks.</summary>
   Task<StoreStatus> GetMeta_StoreStatus();

   /// <summary>Every volume with its copy counters and a live probe of its folder.</summary>
   Task<StoreVolumeInfo[]> GetMeta_StoreVolumes();

   /// <summary>Checks a volume draft (path resolution, write access, free space) without saving it.</summary>
   Task<StoreVolumeValidation> PostGetMeta_StoreVolumeValidate(StoreVolumeSave draft);

   /// <summary>
   /// Creates (<see cref="StoreVolumeSave.Id"/> is <c>null</c>) or updates a volume. The path of a volume
   /// that already holds copies cannot change (409); data is never moved automatically.
   /// </summary>
   Task<StoreVolumeInfo> PostGetMeta_StoreVolumeSave(StoreVolumeSave request);

   /// <summary>Deletes a volume that holds no copies and is not used by any policy (409 otherwise).</summary>
   Task PostMeta_StoreVolumeDelete(string volumeId);

   /// <summary>Every policy with its ordered volumes.</summary>
   Task<StorePolicyInfo[]> GetMeta_StorePolicies();

   /// <summary>
   /// Creates (<see cref="StorePolicySave.Id"/> is <c>null</c>) or updates a policy and its volume list. The access
   /// of a managed policy cannot change (409).
   /// </summary>
   Task<StorePolicyInfo> PostGetMeta_StorePolicySave(StorePolicySave request);

   /// <summary>Deletes a policy that no bucket uses (409 otherwise).</summary>
   Task PostMeta_StorePolicyDelete(string policyId);

   /// <summary>Every bucket with its policy rules and counters.</summary>
   Task<StoreBucketInfo[]> GetMeta_StoreBuckets();

   /// <summary>
   /// Creates (<see cref="StoreBucketSave.Id"/> is <c>null</c>) or updates a bucket. The name of an existing
   /// bucket cannot change. A managed bucket keeps its policy, and no other bucket may take a managed policy (409).
   /// </summary>
   Task<StoreBucketInfo> PostGetMeta_StoreBucketSave(StoreBucketSave request);

   /// <summary>
   /// Deletes a bucket that holds no objects, including deleted ones with history (409 otherwise). A managed
   /// bucket cannot be deleted (409).
   /// </summary>
   Task PostMeta_StoreBucketDelete(string bucketId);

   /// <summary>
   /// Turns a managed bucket that the running API no longer declares into an ordinary bucket; its policy becomes
   /// an ordinary policy and its contents stay. A bucket that is not managed, or still declared, answers 409.
   /// </summary>
   Task PostMeta_StoreBucketRelease(string bucketId);

   /// <summary>
   /// Lists objects whose current version is not a delete marker, ordered by key. With a
   /// <paramref name="delimiter"/>, keys that contain it after <paramref name="prefix"/> are grouped into
   /// <see cref="StoreObjectListing.CommonPrefixes"/>. Pass <see cref="StoreObjectListing.NextContinuation"/>
   /// back as <paramref name="continuation"/> to read the next page.
   /// </summary>
   /// <param name="bucketId">Bucket ID.</param>
   /// <param name="prefix">Only keys starting with this text; <c>null</c> lists all.</param>
   /// <param name="delimiter">Grouping character, usually <c>/</c>; <c>null</c> lists flat.</param>
   /// <param name="continuation">Opaque token of the previous page.</param>
   /// <param name="limit">Maximum number of objects and prefixes together, 1–1000.</param>
   Task<StoreObjectListing> GetMeta_StoreObjects(string bucketId, string? prefix, string? delimiter, string? continuation, int limit);

   /// <summary>One object by key, including when its current version is a delete marker (404 when the key never existed).</summary>
   Task<StoreObjectInfo> GetMeta_StoreObject(string bucketId, string key);

   /// <summary>
   /// Uploads a new version of an object; the content is streamed. Answers 413 when the content exceeds the
   /// policy's maximum object size, 409 when the bucket quota would be exceeded, the bucket is read-only or the
   /// bucket is managed (checked before the content is read), and 400 for an invalid key.
   /// </summary>
   Task<StoreObjectInfo> PostGetMeta_StoreObjectUpload(StoreUploadRequest request, Stream content);

   /// <summary>Content of the current version, or of <paramref name="version"/> when given.</summary>
   Task<Stream> GetMeta_StoreObjectDownload(string bucketId, string key, int? version);

   /// <summary>Deletes an object by adding a delete marker; its history stays and can be restored. 409 for a managed bucket.</summary>
   Task PostMeta_StoreObjectDelete(string bucketId, string key);

   /// <summary>Every version of an object, newest first.</summary>
   Task<StoreObjectVersionInfo[]> GetMeta_StoreObjectVersions(string bucketId, string key);

   /// <summary>Makes an older version current again by adding a new version that points to the same content. 409 for a managed bucket.</summary>
   Task<StoreObjectInfo> PostGetMeta_StoreObjectRestore(string bucketId, string key, int version);

   /// <summary>
   /// Permanently deletes one version. When it was current, the newest remaining version that is not a
   /// delete marker becomes current; when no version remains, the object disappears. 409 for a managed bucket.
   /// </summary>
   Task PostMeta_StoreObjectVersionPurge(string bucketId, string key, int version);

   /// <summary>Dry run of garbage collection: what a run with <paramref name="graceHours"/> (1–720) would delete.</summary>
   Task<StoreGcReview> GetMeta_StoreGcReview(int graceHours);

   /// <summary>Starts garbage collection of unreferenced blobs older than <paramref name="graceHours"/> (1–720) as a global business task.</summary>
   Task<BusinessTaskInfo> PostGetMeta_StoreGcRun(int graceHours);

   /// <summary>Starts version retention on every bucket as a global business task.</summary>
   Task<BusinessTaskInfo> PostGetMeta_StoreRetentionRun();

   /// <summary>
   /// Starts repair as a global business task: checks every copy's file and size, marks lost copies
   /// missing and writes copies until each blob reaches its policy's replica target.
   /// </summary>
   Task<BusinessTaskInfo> PostGetMeta_StoreRepairRun();

   /// <summary>Maintenance tasks that are still alive or failed and not cleared.</summary>
   Task<BusinessTaskInfo[]> GetMeta_StoreTasks();
}

/// <summary>Object storage counters and health.</summary>
/// <param name="Volumes">Number of volumes.</param>
/// <param name="VolumesWithProblems">Volumes that are offline, unreachable or hold missing copies.</param>
/// <param name="Policies">Number of policies.</param>
/// <param name="Buckets">Number of buckets.</param>
/// <param name="Objects">Objects across buckets, including deleted ones with history.</param>
/// <param name="LogicalBytes">Logical bytes of every kept version (what quotas count).</param>
/// <param name="Blobs">Number of distinct content blobs.</param>
/// <param name="PhysicalBytes">Bytes of every ready copy on every volume, after deduplication and with replicas.</param>
/// <param name="MissingCopies">Copies marked missing.</param>
/// <param name="UnderReplicatedBlobs">Referenced blobs with fewer ready copies than their policy's replica target.</param>
/// <param name="OrphanBlobs">Blobs no version refers to.</param>
/// <param name="OrphanBytes">Size of the orphan blobs.</param>
/// <param name="Tasks">Maintenance tasks that are alive, or failed and not cleared.</param>
/// <param name="LastMaintenanceUtc">When background maintenance last finished a pass.</param>
/// <param name="LastMaintenanceMessage">Summary of the last background pass.</param>
public sealed record StoreStatus(int Volumes, int VolumesWithProblems, int Policies, int Buckets, long Objects, long LogicalBytes,
   long Blobs, long PhysicalBytes, long MissingCopies, long UnderReplicatedBlobs, long OrphanBlobs, long OrphanBytes,
   BusinessTaskInfo[] Tasks, DateTime? LastMaintenanceUtc, string? LastMaintenanceMessage);

/// <summary>Result of checking a volume folder.</summary>
/// <param name="IsValid">The folder can hold blobs.</param>
/// <param name="ResolvedPath">Absolute folder path on the API server.</param>
/// <param name="Message">Explanation for the user.</param>
/// <param name="FreeBytes">Free space of the drive, when known.</param>
/// <param name="CanWrite">A test file could be written and deleted.</param>
public sealed record StoreVolumeValidation(bool IsValid, string? ResolvedPath, string Message, long? FreeBytes, bool CanWrite);

/// <summary>One volume with its copy counters.</summary>
/// <param name="Id">Volume ID.</param>
/// <param name="Name">Display name.</param>
/// <param name="State">State.</param>
/// <param name="Type">Kind of storage.</param>
/// <param name="Path">Configured folder path.</param>
/// <param name="Note">Optional note.</param>
/// <param name="Blobs">Ready copies on this volume.</param>
/// <param name="Bytes">Bytes of the ready copies.</param>
/// <param name="Missing">Copies marked missing.</param>
/// <param name="Policies">Policies that use this volume.</param>
/// <param name="Probe">Live check of the folder.</param>
public sealed record StoreVolumeInfo(string Id, string Name, StoreVolumeState State, StoreVolumeType Type, string Path, string? Note,
   long Blobs, long Bytes, long Missing, int Policies, StoreVolumeValidation Probe) {
   /// <inheritdoc/>
   public override string ToString() => Name;
}

/// <summary>Volume to create or update.</summary>
public sealed class StoreVolumeSave
{
   /// <summary>ID of the volume to update; <c>null</c> creates a new one.</summary>
   public string? Id { get; set; }

   /// <summary>Unique display name, 1–100 characters.</summary>
   public string Name { get; set; } = "";

   /// <summary>State.</summary>
   public StoreVolumeState State { get; set; } = StoreVolumeState.Active;

   /// <summary>Kind of storage; only <see cref="StoreVolumeType.LocalFolder"/> exists so far.</summary>
   public StoreVolumeType Type { get; set; } = StoreVolumeType.LocalFolder;

   /// <summary>Folder path, absolute or relative to the API content root.</summary>
   public string Path { get; set; } = "";

   /// <summary>Optional note.</summary>
   public string? Note { get; set; }
}

/// <summary>One policy with its ordered volumes.</summary>
/// <param name="Id">Policy ID.</param>
/// <param name="Name">Display name.</param>
/// <param name="State">State.</param>
/// <param name="Note">Optional note.</param>
/// <param name="ReplicaMin">Copies written before an upload succeeds.</param>
/// <param name="ReplicaTarget">Copies background replication aims for.</param>
/// <param name="KeepVersions">Non-current versions kept per object; <c>null</c> keeps all.</param>
/// <param name="KeepDays">Maximum age of non-current versions in days; <c>null</c> keeps them forever.</param>
/// <param name="QuotaBytes">Logical byte quota per bucket; <c>null</c> is unlimited.</param>
/// <param name="QuotaObjects">Object quota per bucket; <c>null</c> is unlimited.</param>
/// <param name="MaxObjectBytes">Maximum size of one object; <c>null</c> is unlimited.</param>
/// <param name="Access">Public access.</param>
/// <param name="VolumeIds">Volumes in priority order.</param>
/// <param name="Buckets">Buckets using the policy.</param>
/// <param name="ManagedBucket">
/// Name of the managed bucket this policy belongs to; <c>null</c> for an ordinary policy. The access of a
/// managed policy is fixed by the application, and no other bucket can use it.
/// </param>
public sealed record StorePolicyInfo(string Id, string Name, StorePolicyState State, string? Note, int ReplicaMin, int ReplicaTarget,
   int? KeepVersions, int? KeepDays, long? QuotaBytes, long? QuotaObjects, long? MaxObjectBytes, StoreAccess Access,
   string[] VolumeIds, int Buckets, string? ManagedBucket = null) {
   /// <summary>The policy belongs to a managed bucket.</summary>
   public bool IsManaged => ManagedBucket is not null;

   /// <inheritdoc/>
   public override string ToString() => Name;
}

/// <summary>Policy to create or update.</summary>
public sealed class StorePolicySave
{
   /// <summary>ID of the policy to update; <c>null</c> creates a new one.</summary>
   public string? Id { get; set; }

   /// <summary>Unique display name, 1–100 characters.</summary>
   public string Name { get; set; } = "";

   /// <summary>State.</summary>
   public StorePolicyState State { get; set; } = StorePolicyState.Active;

   /// <summary>Optional note.</summary>
   public string? Note { get; set; }

   /// <summary>Copies written before an upload succeeds, at least 1.</summary>
   public int ReplicaMin { get; set; } = 1;

   /// <summary>Copies background replication aims for, at least <see cref="ReplicaMin"/>.</summary>
   public int ReplicaTarget { get; set; } = 1;

   /// <summary>Non-current versions kept per object; <c>null</c> keeps all.</summary>
   public int? KeepVersions { get; set; }

   /// <summary>Maximum age of non-current versions in days; <c>null</c> keeps them forever.</summary>
   public int? KeepDays { get; set; }

   /// <summary>Logical byte quota per bucket; <c>null</c> is unlimited.</summary>
   public long? QuotaBytes { get; set; }

   /// <summary>Object quota per bucket; <c>null</c> is unlimited.</summary>
   public long? QuotaObjects { get; set; }

   /// <summary>Maximum size of one object; <c>null</c> is unlimited.</summary>
   public long? MaxObjectBytes { get; set; }

   /// <summary>Public access.</summary>
   public StoreAccess Access { get; set; } = StoreAccess.Private;

   /// <summary>Volumes in priority order; at least <see cref="ReplicaTarget"/> of them.</summary>
   public string[] VolumeIds { get; set; } = [];
}

/// <summary>One bucket with its policy rules and counters.</summary>
/// <param name="Id">Bucket ID.</param>
/// <param name="Name">Bucket name.</param>
/// <param name="PolicyId">Policy ID.</param>
/// <param name="PolicyName">Policy name.</param>
/// <param name="State">State.</param>
/// <param name="Note">Optional note.</param>
/// <param name="Bytes">Logical bytes of every kept version.</param>
/// <param name="Objects">Objects, including deleted ones with history.</param>
/// <param name="QuotaBytes">Byte quota of the policy.</param>
/// <param name="QuotaObjects">Object quota of the policy.</param>
/// <param name="MaxObjectBytes">Maximum object size of the policy.</param>
/// <param name="Access">Public access of the policy.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Owner">
/// Application (module) that manages the bucket; <c>null</c> for an ordinary bucket. The contents of a managed
/// bucket can only be read and downloaded through the management actions.
/// </param>
/// <param name="Description">What the bucket is for, as declared by its application; read-only for administrators.</param>
/// <param name="IsDeclared">
/// A managed bucket that the running API still declares. A managed bucket that is no longer declared can be
/// released with <see cref="IObjectStorageServices.PostMeta_StoreBucketRelease"/>.
/// </param>
public sealed record StoreBucketInfo(string Id, string Name, string PolicyId, string PolicyName, StoreBucketState State, string? Note,
   long Bytes, long Objects, long? QuotaBytes, long? QuotaObjects, long? MaxObjectBytes, StoreAccess Access, DateTime CreatedAt,
   string? Owner = null, string? Description = null, bool IsDeclared = false) {
   /// <summary>The bucket is managed by an application.</summary>
   public bool IsManaged => Owner is not null;

   /// <summary>A managed bucket that the running API no longer declares.</summary>
   public bool IsUndeclared => IsManaged && !IsDeclared;

   /// <summary>Server-relative public path of the bucket, or <c>null</c> when it is private.</summary>
   public string? PublicPath => Access == StoreAccess.PublicRead ? $"/store/{Name}/" : null;

   /// <inheritdoc/>
   public override string ToString() => Name;
}

/// <summary>Bucket to create or update.</summary>
public sealed class StoreBucketSave
{
   /// <summary>ID of the bucket to update; <c>null</c> creates a new one.</summary>
   public string? Id { get; set; }

   /// <summary>Bucket name (3–63 lowercase letters, digits, <c>-</c> and <c>.</c>); fixed after creation.</summary>
   public string Name { get; set; } = "";

   /// <summary>Policy ID.</summary>
   public string PolicyId { get; set; } = "";

   /// <summary>State.</summary>
   public StoreBucketState State { get; set; } = StoreBucketState.Active;

   /// <summary>Optional note.</summary>
   public string? Note { get; set; }
}

/// <summary>One object with its current version.</summary>
/// <param name="Id">Object ID.</param>
/// <param name="BucketId">Bucket ID.</param>
/// <param name="Key">Object key.</param>
/// <param name="Version">Current version number.</param>
/// <param name="Size">Size of the current version in bytes.</param>
/// <param name="Mime">MIME type of the current version.</param>
/// <param name="IsDeleteMarker">The current version is a delete marker.</param>
/// <param name="ModifiedAt">When the current version was created.</param>
/// <param name="Hash">SHA-256 of the content in lowercase hex; <c>null</c> for a delete marker.</param>
public sealed record StoreObjectInfo(string Id, string BucketId, string Key, int Version, long Size, string Mime, bool IsDeleteMarker,
   DateTime ModifiedAt, string? Hash) {
   /// <summary>Last segment of the key after <c>/</c>.</summary>
   public string Name => Key[(Key.LastIndexOf('/') + 1)..];

   /// <inheritdoc/>
   public override string ToString() => Key;
}

/// <summary>One page of an object listing.</summary>
/// <param name="Prefix">Prefix the listing was limited to.</param>
/// <param name="Delimiter">Grouping character of the listing.</param>
/// <param name="CommonPrefixes">Groups of keys up to and including the delimiter.</param>
/// <param name="Objects">Objects of this page.</param>
/// <param name="NextContinuation">Token for the next page; <c>null</c> on the last page.</param>
public sealed record StoreObjectListing(string? Prefix, string? Delimiter, string[] CommonPrefixes, StoreObjectInfo[] Objects, string? NextContinuation);

/// <summary>One version of an object.</summary>
/// <param name="ObjectId">Object ID.</param>
/// <param name="Version">Version number.</param>
/// <param name="IsCurrent">This is the current version.</param>
/// <param name="IsDeleteMarker">This is a delete marker.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Mime">MIME type.</param>
/// <param name="Hash">SHA-256 of the content in lowercase hex; <c>null</c> for a delete marker.</param>
/// <param name="CreatedAt">When the version was created.</param>
/// <param name="Metadata">User metadata of the version.</param>
public sealed record StoreObjectVersionInfo(string ObjectId, int Version, bool IsCurrent, bool IsDeleteMarker, long Size, string Mime,
   string? Hash, DateTime CreatedAt, Dictionary<string, string>? Metadata) {
   /// <inheritdoc/>
   public override string ToString() => IsDeleteMarker ? $"v{Version} · delete marker" : $"v{Version} · {Size:N0} B";
}

/// <summary>Information accompanying an object upload; the content is sent separately as a stream.</summary>
public sealed class StoreUploadRequest
{
   /// <summary>Bucket ID.</summary>
   public string BucketId { get; set; } = "";

   /// <summary>Object key.</summary>
   public string Key { get; set; } = "";

   /// <summary>MIME type; <c>null</c> stores <c>application/octet-stream</c>.</summary>
   public string? Mime { get; set; }

   /// <summary>User metadata of the new version; at most 32 entries.</summary>
   public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>Dry run of object storage garbage collection.</summary>
/// <param name="GraceHours">Grace period used.</param>
/// <param name="Unreferenced">Blobs still marked ready that no version refers to (marked orphan by a run).</param>
/// <param name="Orphans">Blobs already marked orphan.</param>
/// <param name="OrphanBytes">Size of the orphans and unreferenced blobs.</param>
/// <param name="Eligible">Orphans older than the grace period, deleted by a run.</param>
/// <param name="EligibleBytes">Size of the eligible orphans.</param>
/// <param name="Sample">Up to 100 eligible blobs.</param>
public sealed record StoreGcReview(int GraceHours, int Unreferenced, int Orphans, long OrphanBytes, int Eligible, long EligibleBytes, StoreGcBlob[] Sample);

/// <summary>One blob in a garbage collection review.</summary>
/// <param name="Hash">SHA-256 in lowercase hex.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="OrphanAtUtc">When it became an orphan.</param>
/// <param name="Copies">Physical copies that would be deleted.</param>
public sealed record StoreGcBlob(string Hash, long Size, DateTime? OrphanAtUtc, int Copies);
