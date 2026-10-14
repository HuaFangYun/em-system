using System.Text.Json;
using System.Text.Json.Nodes;
using Em.Api.Core.Models;
using Em.Shared;

namespace Em.Api.Core.ObjectStorage;

/// <summary>A bucket an application module declared with <c>AddManagedBucket</c>.</summary>
/// <param name="Name">Bucket name.</param>
/// <param name="Owner">Application (module) that manages the bucket.</param>
/// <param name="Description">What the bucket is for, written for administrators.</param>
/// <param name="Defaults">Starting values of the bucket's policy.</param>
internal sealed record ManagedBucketDeclaration(string Name, string Owner, string Description, ManagedBucketOptions Defaults)
{
   /// <summary>Policy draft built from <see cref="Defaults"/> on one volume, for validation and creation.</summary>
   public StorePolicySave PolicyDraft(string policyName, string volumeId) => new() {
      Name = policyName, State = StorePolicyState.Active, Access = Defaults.Access, ReplicaMin = 1, ReplicaTarget = 1,
      KeepVersions = Defaults.KeepVersions, KeepDays = Defaults.KeepDays, QuotaBytes = Defaults.QuotaBytes,
      QuotaObjects = Defaults.QuotaObjects, MaxObjectBytes = Defaults.MaxObjectBytes,
      Note = Defaults.Note ?? $"Created for managed bucket '{Name}' of {Owner}.", VolumeIds = [volumeId]
   };

   /// <summary>Checks and normalizes a declaration; throws <see cref="ArgumentException"/> for the first rule it breaks.</summary>
   public static ManagedBucketDeclaration Create(string name, string owner, string description, ManagedBucketOptions defaults) {
      try { StoreNames.Bucket(name); }
      catch (ActionException ex) { throw new ArgumentException($"Managed bucket '{name}': {ex.Message}", nameof(name)); }
      owner = owner?.Trim() ?? "";
      if (owner.Length is 0 or > 100 || owner.Any(char.IsControl))
         throw new ArgumentException($"Managed bucket '{name}': the owner must contain 1–100 characters.", nameof(owner));
      description = description?.Trim() ?? "";
      if (description.Length is < 10 or > 500 || description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
         throw new ArgumentException($"Managed bucket '{name}': the description must contain 10–500 characters and say what the bucket holds " +
            "and which module fills it.", nameof(description));
      if (!Enum.IsDefined(defaults.Access))
         throw new ArgumentException($"Managed bucket '{name}': invalid access.", nameof(defaults));
      var declaration = new ManagedBucketDeclaration(name, owner, description, defaults);
      try {
         var draft = declaration.PolicyDraft(name, "volume");
         draft.Note = StoreNames.Note(draft.Note);
         ObjectStorageServices.ValidatePolicy(draft);
      }
      catch (ActionException ex) { throw new ArgumentException($"Managed bucket '{name}': {ex.Message}", nameof(defaults)); }
      return declaration;
   }
}

/// <summary>
/// The managed buckets declared in this process. Filled by <c>AddManagedBucket</c> while the host is built and read
/// at startup and by the management actions (<see cref="StoreBucketInfo.IsDeclared"/>). One API instance serves a
/// database, so this list is the truth about which managed buckets are still declared.
/// </summary>
internal sealed class ManagedBucketRegistry
{
   private readonly List<ManagedBucketDeclaration> _items = [];

   /// <summary>Declarations in the order they were made.</summary>
   public IReadOnlyList<ManagedBucketDeclaration> Items => _items;

   /// <summary>Adds a declaration; a bucket name declared twice throws <see cref="InvalidOperationException"/>.</summary>
   public void Add(ManagedBucketDeclaration declaration) {
      if (Find(declaration.Name) is { } existing)
         throw new InvalidOperationException($"Managed bucket '{declaration.Name}' is already declared by {existing.Owner}.");
      _items.Add(declaration);
   }

   /// <summary>Declaration of a bucket name, or <c>null</c>.</summary>
   public ManagedBucketDeclaration? Find(string bucketName) => _items.FirstOrDefault(d => d.Name == bucketName);

   /// <summary>The bucket name is declared.</summary>
   public bool IsDeclared(string bucketName) => Find(bucketName) is not null;
}

/// <summary>
/// The managed-bucket keys of <c>ta_StoreBucket.json_object</c>: <c>owner</c> and <c>description</c>. Other keys
/// in the document are kept when it is written.
/// </summary>
internal static class ManagedBucketJson
{
   private const string OwnerKey = "owner";
   private const string DescriptionKey = "description";

   /// <summary>Owner and description stored in a bucket's JSON; both <c>null</c> for an ordinary bucket or unreadable JSON.</summary>
   public static (string? Owner, string? Description) Read(string? json) {
      if (string.IsNullOrWhiteSpace(json)) return (null, null);
      try {
         if (JsonNode.Parse(json) is not JsonObject root) return (null, null);
         return (Text(root, OwnerKey), Text(root, DescriptionKey));
      }
      catch (JsonException) { return (null, null); }
   }

   /// <summary>Owner of a bucket's JSON, or <c>null</c>.</summary>
   public static string? Owner(string? json) => Read(json).Owner;

   /// <summary>Writes owner and description into a bucket's JSON; a <c>null</c> value removes the key, and an empty document becomes <c>null</c>.</summary>
   public static string? Write(string? json, string? owner, string? description) {
      JsonObject root;
      try { root = (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject) ?? []; }
      catch (JsonException) { root = []; }
      Put(root, OwnerKey, owner);
      Put(root, DescriptionKey, description);
      return root.Count == 0 ? null : root.ToJsonString();
   }

   private static string? Text(JsonObject root, string key) =>
      root[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

   private static void Put(JsonObject root, string key, string? value) {
      if (value is null) root.Remove(key);
      else root[key] = value;
   }
}
