using Em.Api.Core.Models;
using Em.Api.Core.ObjectStorage;
using Em.Api.Core.ObjectStorage.Volumes;
using Em.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Em.Api.Core.IntegrationTests;

/// <summary>
/// Managed buckets against SQL Server: provisioning at startup (default volume, own policy, adoption, access and
/// description sync), the 409 locks of the management actions, the module path through <see cref="IObjectStore"/>
/// and release of a bucket that is no longer declared.
/// </summary>
public sealed class ManagedBucketTests(SqlServerDatabase database) : IAsyncLifetime
{
   private const string Description = "Files written by the sample module through IObjectStore; quota changes affect its uploads.";
   private ObjectStoreFixture _fx = null!;
   private static CancellationToken Ct => TestContext.Current.CancellationToken;

   public async ValueTask InitializeAsync() => _fx = await ObjectStoreFixture.CreateAsync(database, Ct);

   public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

   private ObjectStorageRuntime Runtime(string defaultVolumePath = "managed", params ManagedBucketDeclaration[] declarations) {
      var registry = new ManagedBucketRegistry();
      foreach (var declaration in declarations) registry.Add(declaration);
      return new ObjectStorageRuntime([new LocalFolderVolumeProvider(_fx.Folder)]) {
         ManagedBuckets = registry, Options = new ObjectStorageOptions { DefaultVolumePath = defaultVolumePath }
      };
   }

   private static ManagedBucketDeclaration Declare(string name, string owner = "Sample App", string description = Description,
      Action<ManagedBucketOptions>? configure = null) {
      var options = new ManagedBucketOptions();
      configure?.Invoke(options);
      return ManagedBucketDeclaration.Create(name, owner, description, options);
   }

   private Task ProvisionAsync(ObjectStorageRuntime runtime) =>
      new ManagedBucketProvisioner(_fx.NewContext(), runtime, NullLogger.Instance).ProvisionAsync(Ct);

   private ObjectStorageServices Services(ObjectStorageRuntime runtime) {
      var store = new ObjectStore(_fx.NewContext(), runtime);
      return new ObjectStorageServices(_fx.NewContext(), store, runtime, new ObjectStorageMaintenance(store, NullLogger<ObjectStorageMaintenance>.Instance));
   }

   private async Task<(ta_StoreBucket Bucket, ta_StorePolicy Policy)> ReadAsync(string name) {
      var db = _fx.NewContext();
      var bucket = await db.Buckets.SingleAsync(b => b.cStoreBucketName == name, Ct);
      return (bucket, await db.Policies.SingleAsync(p => p.cStorePolicyId == bucket.cStorePolicyId, Ct));
   }

   private static async Task<int> StatusOf(Func<Task> action) => (await Assert.ThrowsAsync<ActionException>(action)).StatusCode;

   [Fact]
   public async Task Provision_CreatesVolumePolicyAndBucket_AndASecondStartKeepsAdminChanges() {
      var runtime = Runtime("managed", Declare("app-files", configure: o => { o.KeepVersions = 3; o.QuotaObjects = 100; }));
      await ProvisionAsync(runtime);

      var (bucket, policy) = await ReadAsync("app-files");
      Assert.Equal(("Sample App", Description), ManagedBucketJson.Read(bucket.json_object));
      Assert.Equal("app-files (managed)", policy.cStorePolicyName);
      Assert.Equal((int)StoreAccess.Private, policy.cStorePolicyAccess);
      Assert.Equal(3, policy.cStorePolicyKeepVersions);
      Assert.Equal(100, policy.cStorePolicyQuotaObjects);
      var db = _fx.NewContext();
      var volumeId = (await db.Meta.SingleAsync(m => m.cMetaKey == ManagedBucketProvisioner.DefaultVolumeKey, Ct)).cMetaValue;
      var volume = await db.Volumes.SingleAsync(v => v.cStoreVolumeId == volumeId, Ct);
      Assert.Equal(("managed-default", "managed"), (volume.cStoreVolumeName, volume.cStoreVolumePath));
      Assert.Equal([volumeId], await db.PolicyVolumes.Where(v => v.cStorePolicyId == policy.cStorePolicyId).Select(v => v.cStoreVolumeId).ToListAsync(Ct));

      // An administrator changes infrastructure values; the next startup keeps them.
      var services = Services(runtime);
      var info = (await services.GetMeta_StorePolicies()).Single(p => p.Id == policy.cStorePolicyId);
      Assert.Equal("app-files", info.ManagedBucket);
      await services.PostGetMeta_StorePolicySave(new StorePolicySave {
         Id = info.Id, Name = info.Name, Access = info.Access, KeepVersions = 7, QuotaObjects = 500, VolumeIds = [volumeId, _fx.VolumeA], ReplicaTarget = 2
      });
      var listed = (await services.GetMeta_StoreBuckets()).Single(b => b.Name == "app-files");
      Assert.True(listed.IsManaged);
      Assert.True(listed.IsDeclared);
      Assert.Equal(Description, listed.Description);
      await services.PostGetMeta_StoreBucketSave(new StoreBucketSave { Id = listed.Id, PolicyId = listed.PolicyId, State = StoreBucketState.ReadOnly, Note = "admin note" });

      var policies = await db.Policies.CountAsync(Ct);
      var volumes = await db.Volumes.CountAsync(Ct);
      await ProvisionAsync(runtime);
      (bucket, policy) = await ReadAsync("app-files");
      Assert.Equal(7, policy.cStorePolicyKeepVersions);
      Assert.Equal(500, policy.cStorePolicyQuotaObjects);
      Assert.Equal(2, policy.cStorePolicyReplicaTarget);
      Assert.Equal("admin note", bucket.cStoreBucketNote);
      Assert.Equal((int)StoreBucketState.ReadOnly, bucket.cStoreBucketState);
      Assert.Equal(policies, await _fx.NewContext().Policies.CountAsync(Ct));
      Assert.Equal(volumes, await _fx.NewContext().Volumes.CountAsync(Ct));
   }

   [Fact]
   public async Task Provision_EnforcesDeclaredAccess_AndSyncsOwnerAndDescription_ButNotTheNote() {
      await ProvisionAsync(Runtime("managed", Declare("pub-files", configure: o => o.Access = StoreAccess.PublicRead)));
      var (bucket, policy) = await ReadAsync("pub-files");
      Assert.Equal((int)StoreAccess.PublicRead, policy.cStorePolicyAccess);

      var db = _fx.NewContext();
      await db.Policies.Where(p => p.cStorePolicyId == policy.cStorePolicyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.cStorePolicyAccess, 1), Ct);
      await db.Buckets.Where(b => b.cStoreBucketId == bucket.cStoreBucketId).ExecuteUpdateAsync(s => s.SetProperty(b => b.cStoreBucketNote, "keep me"), Ct);

      const string newDescription = "Public images of the renamed module, served at /store; keep the access public.";
      await ProvisionAsync(Runtime("managed", Declare("pub-files", "Renamed App", newDescription, o => o.Access = StoreAccess.PublicRead)));
      (bucket, policy) = await ReadAsync("pub-files");
      Assert.Equal((int)StoreAccess.PublicRead, policy.cStorePolicyAccess);
      Assert.Equal(("Renamed App", newDescription), ManagedBucketJson.Read(bucket.json_object));
      Assert.Equal("keep me", bucket.cStoreBucketNote);
   }

   [Fact]
   public async Task Provision_AdoptsOrdinaryBucket_KeepsContents_AndOwnPolicy() {
      var name = await _fx.AddBucketAsync(Ct);
      await _fx.NewStore().PutAsync(name, "old.txt", ObjectStoreFixture.Content("before"), ct: Ct);
      var (_, before) = await ReadAsync(name);

      await ProvisionAsync(Runtime("managed", Declare(name)));
      var (bucket, policy) = await ReadAsync(name);
      Assert.Equal("Sample App", ManagedBucketJson.Owner(bucket.json_object));
      Assert.Equal(before.cStorePolicyId, policy.cStorePolicyId);
      Assert.Equal("before", await ObjectStoreFixture.ReadAsync((await _fx.NewStore().OpenAsync(name, "old.txt", ct: Ct)).Content, Ct));
      Assert.False(await _fx.NewContext().Meta.AnyAsync(m => m.cMetaKey == ManagedBucketProvisioner.DefaultVolumeKey, Ct));
   }

   [Fact]
   public async Task Provision_AdoptsBucketOnSharedPolicy_ByCopyingThePolicy() {
      var shared = await _fx.AddBucketAsync(Ct, keepVersions: 4, volumes: [_fx.VolumeB, _fx.VolumeA]);
      var (sharedBucket, sharedPolicy) = await ReadAsync(shared);
      var db = _fx.NewContext();
      var now = DateTime.UtcNow;
      db.Buckets.Add(new ta_StoreBucket {
         cStoreBucketId = Ulid.NewUlid().ToString(), cStoreBucketName = "adopt-me", cStorePolicyId = sharedPolicy.cStorePolicyId,
         cStoreBucketState = 1, ustamp = now, datestamp = now
      });
      await db.SaveChangesAsync(Ct);

      await ProvisionAsync(Runtime("managed", Declare("adopt-me")));
      var (_, copy) = await ReadAsync("adopt-me");
      Assert.NotEqual(sharedPolicy.cStorePolicyId, copy.cStorePolicyId);
      Assert.Equal("adopt-me (managed)", copy.cStorePolicyName);
      Assert.Equal(4, copy.cStorePolicyKeepVersions);
      var volumes = await _fx.NewContext().PolicyVolumes.Where(v => v.cStorePolicyId == copy.cStorePolicyId)
         .OrderBy(v => v.cStorePolicyVolumeOrder).Select(v => v.cStoreVolumeId).ToListAsync(Ct);
      Assert.Equal([_fx.VolumeB, _fx.VolumeA], volumes);
      Assert.Equal(sharedPolicy.cStorePolicyId, (await ReadAsync(shared)).Bucket.cStorePolicyId);
      Assert.Null(ManagedBucketJson.Owner((await ReadAsync(shared)).Bucket.json_object));
      Assert.Equal(sharedBucket.cStoreBucketId, (await ReadAsync(shared)).Bucket.cStoreBucketId);
   }

   [Fact]
   public async Task Provision_RecreatesDeletedDefaultVolume_WithFreeName() {
      await ProvisionAsync(Runtime("managed", Declare("first-files")));
      var db = _fx.NewContext();
      await db.Meta.Where(m => m.cMetaKey == ManagedBucketProvisioner.DefaultVolumeKey)
         .ExecuteUpdateAsync(s => s.SetProperty(m => m.cMetaValue, Ulid.NewUlid().ToString()), Ct);

      await ProvisionAsync(Runtime("managed2", Declare("first-files"), Declare("second-files")));
      var (_, policy) = await ReadAsync("second-files");
      var volumeId = await _fx.NewContext().PolicyVolumes.Where(v => v.cStorePolicyId == policy.cStorePolicyId).Select(v => v.cStoreVolumeId).SingleAsync(Ct);
      var volume = await _fx.NewContext().Volumes.SingleAsync(v => v.cStoreVolumeId == volumeId, Ct);
      Assert.Equal(("managed-default-2", "managed2"), (volume.cStoreVolumeName, volume.cStoreVolumePath));
   }

   [Fact]
   public async Task Provision_FailsWithClearMessage_WhenDefaultVolumeFolderIsUnusable() {
      var root = Path.GetPathRoot(_fx.Folder)!;
      var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(Runtime(root, Declare("bad-files"))));
      Assert.Contains("bad-files", error.Message);
      Assert.Contains("DefaultVolumePath", error.Message);
      Assert.False(await _fx.NewContext().Buckets.AnyAsync(b => b.cStoreBucketName == "bad-files", Ct));
   }

   [Fact]
   public async Task ManagementActions_RefuseChangesToManagedBucket_ButAllowReadingAndTheModulePath() {
      var runtime = Runtime("managed", Declare("app-files"));
      await ProvisionAsync(runtime);
      var put = await new ObjectStore(_fx.NewContext(), runtime).PutAsync("app-files", "doc.txt", ObjectStoreFixture.Content("v1"), ct: Ct);
      await new ObjectStore(_fx.NewContext(), runtime).PutAsync("app-files", "doc.txt", ObjectStoreFixture.Content("v2"), ct: Ct);
      var services = Services(runtime);
      var bucket = (await services.GetMeta_StoreBuckets()).Single(b => b.Name == "app-files");
      var other = await _fx.AddBucketAsync(Ct);
      var otherInfo = (await services.GetMeta_StoreBuckets()).Single(b => b.Name == other);

      // The body must not be read when the upload is refused.
      Assert.Equal(409, await StatusOf(() => services.PostGetMeta_StoreObjectUpload(new StoreUploadRequest { BucketId = bucket.Id, Key = "x" }, new UnreadableStream())));
      Assert.Equal(409, await StatusOf(() => services.PostMeta_StoreObjectDelete(bucket.Id, "doc.txt")));
      Assert.Equal(409, await StatusOf(() => services.PostGetMeta_StoreObjectRestore(bucket.Id, "doc.txt", 1)));
      Assert.Equal(409, await StatusOf(() => services.PostMeta_StoreObjectVersionPurge(bucket.Id, "doc.txt", 1)));
      Assert.Equal(409, await StatusOf(() => services.PostMeta_StoreBucketDelete(bucket.Id)));
      Assert.Equal(409, await StatusOf(() => services.PostGetMeta_StoreBucketSave(new StoreBucketSave { Id = bucket.Id, PolicyId = otherInfo.PolicyId })));
      Assert.Equal(409, await StatusOf(() => services.PostGetMeta_StoreBucketSave(new StoreBucketSave { Name = "new-files", PolicyId = bucket.PolicyId })));
      Assert.Equal(409, await StatusOf(() => services.PostGetMeta_StoreBucketSave(new StoreBucketSave { Id = otherInfo.Id, PolicyId = bucket.PolicyId })));
      var policy = (await services.GetMeta_StorePolicies()).Single(p => p.Id == bucket.PolicyId);
      Assert.Equal(409, await StatusOf(() => services.PostGetMeta_StorePolicySave(new StorePolicySave {
         Id = policy.Id, Name = policy.Name, Access = StoreAccess.PublicRead, VolumeIds = policy.VolumeIds
      })));
      Assert.Equal(409, await StatusOf(() => services.PostMeta_StorePolicyDelete(policy.Id)));
      Assert.Equal((int)StoreAccess.Private, (await ReadAsync("app-files")).Policy.cStorePolicyAccess);

      Assert.Single((await services.GetMeta_StoreObjects(bucket.Id, null, null, null, 100)).Objects);
      Assert.Equal(put.Key, (await services.GetMeta_StoreObject(bucket.Id, "doc.txt")).Key);
      Assert.Equal(2, (await services.GetMeta_StoreObjectVersions(bucket.Id, "doc.txt")).Length);
      Assert.Equal("v1", await ObjectStoreFixture.ReadAsync(await services.GetMeta_StoreObjectDownload(bucket.Id, "doc.txt", 1), Ct));

      // Contents of an ordinary bucket still change through the same actions.
      await services.PostGetMeta_StoreObjectUpload(new StoreUploadRequest { BucketId = otherInfo.Id, Key = "free.txt" }, ObjectStoreFixture.Content("ok"));
   }

   [Fact]
   public async Task Release_OnlyForManagedBucketsThatAreNoLongerDeclared() {
      await ProvisionAsync(Runtime("managed", Declare("old-files")));
      var declared = Services(Runtime("managed", Declare("old-files")));
      var bucket = (await declared.GetMeta_StoreBuckets()).Single(b => b.Name == "old-files");
      Assert.Equal(409, await StatusOf(() => declared.ReleaseBucketCoreAsync(bucket.Id)));

      var undeclared = Services(Runtime());
      var listed = (await undeclared.GetMeta_StoreBuckets()).Single(b => b.Name == "old-files");
      Assert.True(listed.IsUndeclared);
      Assert.Equal(409, await StatusOf(() => undeclared.PostMeta_StoreBucketDelete(listed.Id)));
      Assert.Equal(("old-files", "Sample App"), await undeclared.ReleaseBucketCoreAsync(listed.Id));

      listed = (await undeclared.GetMeta_StoreBuckets()).Single(b => b.Name == "old-files");
      Assert.False(listed.IsManaged);
      Assert.Equal(Description, listed.Description);
      Assert.Null((await undeclared.GetMeta_StorePolicies()).Single(p => p.Id == listed.PolicyId).ManagedBucket);
      Assert.Equal(409, await StatusOf(() => undeclared.ReleaseBucketCoreAsync(listed.Id)));
      Assert.Equal(404, await StatusOf(() => undeclared.ReleaseBucketCoreAsync(Ulid.NewUlid().ToString())));
   }

   [Fact]
   public async Task BucketView_ExpandsOwnerAndDescription() {
      await ProvisionAsync(Runtime("managed", Declare("view-files")));
      await using var connection = new SqlConnection(_fx.ConnectionString);
      await connection.OpenAsync(Ct);
      await using var command = new SqlCommand("SELECT cvStoreBucketOwner, cvStoreBucketDescription FROM dbo.vi_StoreBucket WHERE cStoreBucketName = 'view-files'", connection);
      await using var reader = await command.ExecuteReaderAsync(Ct);
      Assert.True(await reader.ReadAsync(Ct));
      Assert.Equal("Sample App", reader.GetString(0));
      Assert.Equal(Description, reader.GetString(1));
   }

   /// <summary>A request body that fails the test when anything reads it.</summary>
   private sealed class UnreadableStream : Stream
   {
      public override bool CanRead => true;
      public override bool CanSeek => false;
      public override bool CanWrite => false;
      public override long Length => throw new NotSupportedException();
      public override long Position { get => 0; set => throw new NotSupportedException(); }
      public override void Flush() { }
      public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The upload body was read.");
      public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
         throw new InvalidOperationException("The upload body was read.");
      public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
         throw new InvalidOperationException("The upload body was read.");
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
      public override void SetLength(long value) => throw new NotSupportedException();
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
   }
}
