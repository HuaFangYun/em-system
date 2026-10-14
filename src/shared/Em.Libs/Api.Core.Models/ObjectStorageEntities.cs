using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Em.Api.Core.Models;

/// <summary>State of a storage volume.</summary>
public enum StoreVolumeState
{
   /// <summary>New blobs may be written and existing blobs read.</summary>
   Active = 1,

   /// <summary>Existing blobs are read; nothing new is written.</summary>
   ReadOnly = 2,

   /// <summary>The volume is skipped for reads and writes.</summary>
   Offline = 3
}

/// <summary>Kind of storage behind a volume.</summary>
public enum StoreVolumeType
{
   /// <summary>A folder on the API server's file system (local disk or a mounted NAS share).</summary>
   LocalFolder = 1
}

/// <summary>State of a storage policy.</summary>
public enum StorePolicyState
{
   /// <summary>The policy can be assigned to buckets.</summary>
   Active = 1,

   /// <summary>Existing buckets keep the policy, but it cannot be assigned to new buckets.</summary>
   Disabled = 2
}

/// <summary>Who may read the objects of a bucket through the public <c>/store</c> path.</summary>
public enum StoreAccess
{
   /// <summary>Objects are only reachable through the management actions or server-side code.</summary>
   Private = 1,

   /// <summary>Objects are readable by anyone through <c>GET /store/&lt;bucket&gt;/&lt;key&gt;</c>.</summary>
   PublicRead = 2
}

/// <summary>State of a bucket.</summary>
public enum StoreBucketState
{
   /// <summary>Objects may be uploaded, deleted and restored.</summary>
   Active = 1,

   /// <summary>Objects can be read but not changed.</summary>
   ReadOnly = 2
}

/// <summary>State of a content blob.</summary>
public enum StoreBlobState
{
   /// <summary>At least one version refers to the blob, or a new upload is about to.</summary>
   Ready = 1,

   /// <summary>No version refers to the blob; garbage collection deletes it after the grace period.</summary>
   Orphan = 2
}

/// <summary>State of one physical copy of a blob on a volume.</summary>
public enum StoreBlobCopyState
{
   /// <summary>The copy is being written.</summary>
   Writing = 1,

   /// <summary>The copy is complete and verified.</summary>
   Ready = 2,

   /// <summary>The file was not found or did not match; repair replaces it.</summary>
   Missing = 3,

   /// <summary>The copy is being deleted by garbage collection.</summary>
   Deleting = 4
}

/// <summary>Row of table <c>ta_StoreVolume</c>: a physical location that holds blob files.</summary>
[Table("ta_StoreVolume")]
public class ta_StoreVolume
{
   /// <summary>Primary key (ULID).</summary>
   [Key] public string cStoreVolumeId { get; set; } = "";

   /// <summary>Unique display name.</summary>
   public string cStoreVolumeName { get; set; } = "";

   /// <summary>State, see <see cref="StoreVolumeState"/>.</summary>
   public int cStoreVolumeState { get; set; }

   /// <summary>Kind, see <see cref="StoreVolumeType"/>.</summary>
   public int cStoreVolumeType { get; set; }

   /// <summary>Folder path, absolute or relative to the API content root. Never holds secrets.</summary>
   public string cStoreVolumePath { get; set; } = "";

   /// <summary>Optional note.</summary>
   public string? cStoreVolumeNote { get; set; }

   /// <summary>Last update time.</summary>
   public DateTime ustamp { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }

   /// <summary>Additional data as JSON, reserved for remote provider settings.</summary>
   public string? json_object { get; set; }
}

/// <summary>Row of table <c>ta_StorePolicy</c>: placement, replica, retention, quota and access rules of buckets.</summary>
[Table("ta_StorePolicy")]
public class ta_StorePolicy
{
   /// <summary>Primary key (ULID).</summary>
   [Key] public string cStorePolicyId { get; set; } = "";

   /// <summary>Unique display name.</summary>
   public string cStorePolicyName { get; set; } = "";

   /// <summary>State, see <see cref="StorePolicyState"/>.</summary>
   public int cStorePolicyState { get; set; }

   /// <summary>Optional note.</summary>
   public string? cStorePolicyNote { get; set; }

   /// <summary>Copies that must be written before an upload succeeds.</summary>
   public int cStorePolicyReplicaMin { get; set; } = 1;

   /// <summary>Copies the background replication aims for.</summary>
   public int cStorePolicyReplicaTarget { get; set; } = 1;

   /// <summary>Number of non-current versions kept per object; <c>null</c> keeps all.</summary>
   public int? cStorePolicyKeepVersions { get; set; }

   /// <summary>Maximum age in days of non-current versions; <c>null</c> keeps them forever.</summary>
   public int? cStorePolicyKeepDays { get; set; }

   /// <summary>Maximum logical bytes of all kept versions in a bucket; <c>null</c> means unlimited.</summary>
   public long? cStorePolicyQuotaBytes { get; set; }

   /// <summary>Maximum number of objects in a bucket; <c>null</c> means unlimited.</summary>
   public long? cStorePolicyQuotaObjects { get; set; }

   /// <summary>Maximum size of one uploaded object; <c>null</c> means unlimited.</summary>
   public long? cStorePolicyMaxObjectBytes { get; set; }

   /// <summary>Public access, see <see cref="StoreAccess"/>.</summary>
   public int cStorePolicyAccess { get; set; } = 1;

   /// <summary>Last update time.</summary>
   public DateTime ustamp { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }

   /// <summary>Additional data as JSON.</summary>
   public string? json_object { get; set; }
}

/// <summary>Row of table <c>ta_StorePolicyVolume</c>: a volume a policy may place blobs on, in priority order.</summary>
[Table("ta_StorePolicyVolume")]
public class ta_StorePolicyVolume
{
   /// <summary>Key of the related <c>ta_StorePolicy</c> row.</summary>
   public string cStorePolicyId { get; set; } = "";

   /// <summary>Key of the related <c>ta_StoreVolume</c> row.</summary>
   public string cStoreVolumeId { get; set; } = "";

   /// <summary>Priority; lower is used first.</summary>
   public int cStorePolicyVolumeOrder { get; set; }
}

/// <summary>Row of table <c>ta_StoreBucket</c>: a named container of objects.</summary>
[Table("ta_StoreBucket")]
public class ta_StoreBucket
{
   /// <summary>Primary key (ULID).</summary>
   [Key] public string cStoreBucketId { get; set; } = "";

   /// <summary>Unique bucket name following the S3 naming rules.</summary>
   public string cStoreBucketName { get; set; } = "";

   /// <summary>Key of the related <c>ta_StorePolicy</c> row.</summary>
   public string cStorePolicyId { get; set; } = "";

   /// <summary>State, see <see cref="StoreBucketState"/>.</summary>
   public int cStoreBucketState { get; set; }

   /// <summary>Optional note.</summary>
   public string? cStoreBucketNote { get; set; }

   /// <summary>Logical bytes of every kept version; updated in the same transaction as the versions.</summary>
   public long cStoreBucketBytes { get; set; }

   /// <summary>Number of objects (keys), including objects whose current version is a delete marker.</summary>
   public long cStoreBucketObjects { get; set; }

   /// <summary>Last update time.</summary>
   public DateTime ustamp { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }

   /// <summary>
   /// Additional data as JSON. A managed bucket keeps <c>owner</c> (the application that manages it) and
   /// <c>description</c> (what the bucket is for) here; without <c>owner</c> the bucket is an ordinary bucket.
   /// </summary>
   public string? json_object { get; set; }
}

/// <summary>Row of table <c>ta_StoreObject</c>: one key in a bucket.</summary>
[Table("ta_StoreObject")]
public class ta_StoreObject
{
   /// <summary>Primary key (ULID).</summary>
   [Key] public string cStoreObjectId { get; set; } = "";

   /// <summary>Key of the related <c>ta_StoreBucket</c> row.</summary>
   public string cStoreBucketId { get; set; } = "";

   /// <summary>Object key, case-sensitive.</summary>
   public string cStoreObjectKey { get; set; } = "";

   /// <summary>SHA-256 of the UTF-8 key; unique per bucket.</summary>
   public byte[] cStoreObjectKeyHash { get; set; } = [];

   /// <summary>Current version number; <c>null</c> only inside the transaction that creates the object.</summary>
   public int? cStoreObjectVersionCurrent { get; set; }

   /// <summary>Next version number to allocate; never decreases.</summary>
   public int cStoreObjectVersionNext { get; set; } = 1;

   /// <summary>Record state.</summary>
   public int cStoreObjectState { get; set; }

   /// <summary>Last update time.</summary>
   public DateTime ustamp { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }

   /// <summary>Additional data as JSON.</summary>
   public string? json_object { get; set; }
}

/// <summary>Row of table <c>ta_StoreObjectVersion</c>: one version of an object.</summary>
[Table("ta_StoreObjectVersion")]
public class ta_StoreObjectVersion
{
   /// <summary>Key of the related <c>ta_StoreObject</c> row.</summary>
   public string cStoreObjectId { get; set; } = "";

   /// <summary>Version number, local to the object.</summary>
   public int cStoreObjectVersionId { get; set; }

   /// <summary>Content blob; <c>null</c> for a delete marker.</summary>
   public byte[]? cStoreObjectBlobHash { get; set; }

   /// <summary>Logical size in bytes; 0 for a delete marker.</summary>
   public long cStoreObjectVersionSize { get; set; }

   /// <summary>MIME type of the content.</summary>
   public string cStoreObjectVersionMime { get; set; } = "";

   /// <summary><c>true</c> for a delete marker.</summary>
   public bool cStoreObjectVersionMarker { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }

   /// <summary>User metadata of the version as a JSON object of strings.</summary>
   public string? json_object { get; set; }
}

/// <summary>Row of table <c>ta_StoreObjectBlob</c>: content identified by its SHA-256, shared across buckets.</summary>
[Table("ta_StoreObjectBlob")]
public class ta_StoreObjectBlob
{
   /// <summary>SHA-256 of the content.</summary>
   [Key] public byte[] cStoreObjectBlobHash { get; set; } = [];

   /// <summary>CRC32 of the content, for quick verification of copies.</summary>
   public byte[] cStoreObjectBlobCrc32 { get; set; } = [];

   /// <summary>Size in bytes.</summary>
   public long cStoreObjectBlobSize { get; set; }

   /// <summary>State, see <see cref="StoreBlobState"/>.</summary>
   public int cStoreObjectBlobState { get; set; }

   /// <summary>When the blob became an orphan; <c>null</c> while it is <see cref="StoreBlobState.Ready"/>.</summary>
   public DateTime? cStoreObjectBlobOrphanAt { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }
}

/// <summary>Row of table <c>ta_StoreObjectBlobVol</c>: one physical copy of a blob on a volume.</summary>
[Table("ta_StoreObjectBlobVol")]
public class ta_StoreObjectBlobVol
{
   /// <summary>Key of the related <c>ta_StoreObjectBlob</c> row.</summary>
   public byte[] cStoreObjectBlobHash { get; set; } = [];

   /// <summary>Key of the related <c>ta_StoreVolume</c> row.</summary>
   public string cStoreVolumeId { get; set; } = "";

   /// <summary>State, see <see cref="StoreBlobCopyState"/>.</summary>
   public int cStoreObjectBlobVolState { get; set; }

   /// <summary>Last time the copy was checked against the blob's size.</summary>
   public DateTime? cStoreObjectBlobVolVerifiedAt { get; set; }

   /// <summary>Creation time.</summary>
   public DateTime datestamp { get; set; }
}
