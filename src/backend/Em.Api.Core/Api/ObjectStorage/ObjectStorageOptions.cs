using Em.Api.Core.Models;

namespace Em.Api.Core.ObjectStorage;

/// <summary>Host options of object storage, set through <c>AddObjectStorage(o =&gt; ...)</c>.</summary>
public sealed class ObjectStorageOptions
{
   /// <summary>Folder used when <see cref="DefaultVolumePath"/> is not set.</summary>
   public const string DefaultVolumeFallback = "data/objectstore";

   /// <summary>
   /// Folder of the volume that object storage creates for managed buckets, absolute or relative to the API
   /// content root. The volume is created once, only when a managed bucket needs a new policy; administrators
   /// can add volumes to that policy or move it to others afterwards. Defaults to <c>data/objectstore</c>.
   /// </summary>
   public string DefaultVolumePath { get; set; } = DefaultVolumeFallback;
}

/// <summary>
/// Starting values of the policy that object storage creates for a managed bucket. They are used only when that
/// policy is created; later changes by an administrator are kept. Only <see cref="Access"/> is enforced at every
/// startup.
/// </summary>
public sealed class ManagedBucketOptions
{
   /// <summary>Public access of the bucket, enforced at every startup. Defaults to <see cref="StoreAccess.Private"/>.</summary>
   public StoreAccess Access { get; set; } = StoreAccess.Private;

   /// <summary>Largest single object in bytes; <c>null</c> is unlimited.</summary>
   public long? MaxObjectBytes { get; set; }

   /// <summary>Non-current versions kept per object (0–100000); <c>null</c> keeps all.</summary>
   public int? KeepVersions { get; set; }

   /// <summary>Maximum age of non-current versions in days (1–36500); <c>null</c> keeps them forever.</summary>
   public int? KeepDays { get; set; }

   /// <summary>Logical byte quota of the bucket; <c>null</c> is unlimited.</summary>
   public long? QuotaBytes { get; set; }

   /// <summary>Object quota of the bucket; <c>null</c> is unlimited.</summary>
   public long? QuotaObjects { get; set; }

   /// <summary>Note of the created policy, up to 500 characters; <c>null</c> writes a note naming the bucket and its application.</summary>
   public string? Note { get; set; }
}
