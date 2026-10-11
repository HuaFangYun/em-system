using Em.Api.Core.Models;
using Em.Api.Core.ObjectStorage;
using Em.Shared;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.IntegrationTests;

/// <summary>
/// Object storage against SQL Server and real folders: upload and deduplication, versions, delete markers,
/// purge, quotas, replicas and repair, garbage collection, concurrent uploads, leftovers of an interrupted
/// upload and the bucket counters.
/// </summary>
public sealed class ObjectStoreTests(SqlServerDatabase database) : IAsyncLifetime
{
   private ObjectStoreFixture _fx = null!;
   private static CancellationToken Ct => TestContext.Current.CancellationToken;

   public async ValueTask InitializeAsync() => _fx = await ObjectStoreFixture.CreateAsync(database, Ct);

   public async ValueTask DisposeAsync() => await _fx.DisposeAsync();

   private async Task AssertCountersMatchAsync(string bucketName) {
      var db = _fx.NewContext();
      var bucket = await db.Buckets.SingleAsync(b => b.cStoreBucketName == bucketName, Ct);
      var objects = db.Objects.Where(o => o.cStoreBucketId == bucket.cStoreBucketId);
      var bytes = await db.Versions.Where(v => objects.Any(o => o.cStoreObjectId == v.cStoreObjectId)).SumAsync(v => (long?)v.cStoreObjectVersionSize, Ct) ?? 0;
      Assert.Equal(bytes, bucket.cStoreBucketBytes);
      Assert.Equal(await objects.LongCountAsync(Ct), bucket.cStoreBucketObjects);
   }

   [Fact]
   public async Task Upload_DeduplicatesContentAcrossKeys() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var store = _fx.NewStore();
      var first = await store.PutAsync(bucket, "a.txt", ObjectStoreFixture.Content("same content"), "text/plain", ct: Ct);
      var second = await store.PutAsync(bucket, "folder/b.txt", ObjectStoreFixture.Content("same content"), ct: Ct);

      Assert.Equal(first.Hash, second.Hash);
      Assert.Equal(StoreNames.DefaultMime, second.Mime);
      var db = _fx.NewContext();
      var hash = StoreNames.ParseHash(first.Hash);
      Assert.Equal(1, await db.Blobs.CountAsync(b => b.cStoreObjectBlobHash == hash, Ct));
      Assert.Equal(1, await db.Copies.CountAsync(c => c.cStoreObjectBlobHash == hash && c.cStoreObjectBlobVolState == 2, Ct));
      Assert.Equal((int)StoreBlobState.Ready, (await db.Blobs.SingleAsync(b => b.cStoreObjectBlobHash == hash, Ct)).cStoreObjectBlobState);
      Assert.True(File.Exists(_fx.BlobPath("A", first.Hash!)));
      Assert.Equal("same content", await ObjectStoreFixture.ReadAsync((await store.OpenAsync(bucket, "folder/b.txt", ct: Ct)).Content, Ct));
      Assert.Empty(Directory.EnumerateFiles(Path.Combine(_fx.Folder, "A", ".staging")));
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task Versions_RestoreAddsNewVersionPointingToOldContent() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var store = _fx.NewStore();
      var v1 = await store.PutAsync(bucket, "doc", ObjectStoreFixture.Content("one"), metadata: new Dictionary<string, string> { ["author"] = "x" }, ct: Ct);
      var v2 = await store.PutAsync(bucket, "doc", ObjectStoreFixture.Content("two"), ct: Ct);
      Assert.Equal(1, v1.Version);
      Assert.Equal(2, v2.Version);

      var restored = await store.RestoreAsync(bucket, "doc", 1, Ct);
      Assert.Equal(3, restored.Version);
      Assert.Equal(v1.Hash, restored.Hash);
      Assert.Equal("one", await ObjectStoreFixture.ReadAsync((await store.OpenAsync(bucket, "doc", ct: Ct)).Content, Ct));
      Assert.Equal("two", await ObjectStoreFixture.ReadAsync((await store.OpenAsync(bucket, "doc", 2, Ct)).Content, Ct));
      var versions = await store.VersionsAsync(bucket, "doc", Ct);
      Assert.Equal([3, 2, 1], versions.Select(v => v.Version));
      Assert.True(versions[0].IsCurrent);
      Assert.Equal("x", versions[0].Metadata!["author"]);
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task Delete_AddsMarker_HidesFromListing_AndCanBeRestored() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var store = _fx.NewStore();
      await store.PutAsync(bucket, "x/1", ObjectStoreFixture.Content("1"), ct: Ct);
      await store.PutAsync(bucket, "x/2", ObjectStoreFixture.Content("2"), ct: Ct);

      var marker = await store.DeleteAsync(bucket, "x/1", Ct);
      Assert.True(marker.IsDeleteMarker);
      Assert.Equal(2, marker.Version);
      Assert.Equal(2, (await store.DeleteAsync(bucket, "x/1", Ct)).Version); // already deleted: unchanged
      var missing = await Assert.ThrowsAsync<ActionException>(() => store.OpenAsync(bucket, "x/1", ct: Ct));
      Assert.Equal(404, missing.StatusCode);
      Assert.Equal(["x/2"], (await store.ListAsync(bucket, "x/", ct: Ct)).Objects.Select(o => o.Key));

      await store.RestoreAsync(bucket, "x/1", 1, Ct);
      Assert.Equal(["x/1", "x/2"], (await store.ListAsync(bucket, "x/", ct: Ct)).Objects.Select(o => o.Key));
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task Purge_CurrentVersion_PromotesNewestNonMarker_AndLastPurgeRemovesObject() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var store = _fx.NewStore();
      await store.PutAsync(bucket, "k", ObjectStoreFixture.Content("v1"), ct: Ct);
      await store.PutAsync(bucket, "k", ObjectStoreFixture.Content("v2-unique"), ct: Ct);
      await store.DeleteAsync(bucket, "k", Ct); // v3 marker

      await store.PurgeVersionAsync(bucket, "k", 3, Ct);
      Assert.Equal(2, (await store.GetAsync(bucket, "k", Ct))!.Version);
      await store.PurgeVersionAsync(bucket, "k", 2, Ct);
      Assert.Equal(1, (await store.GetAsync(bucket, "k", Ct))!.Version);

      var db = _fx.NewContext();
      var orphan = StoreNames.ParseHash(StoreNames.Hex(System.Security.Cryptography.SHA256.HashData("v2-unique"u8.ToArray())));
      Assert.Equal((int)StoreBlobState.Orphan, (await db.Blobs.SingleAsync(b => b.cStoreObjectBlobHash == orphan, Ct)).cStoreObjectBlobState);

      await store.PurgeVersionAsync(bucket, "k", 1, Ct);
      Assert.Null(await store.GetAsync(bucket, "k", Ct));
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task Quota_RejectsUploadThatWouldExceedIt_WithoutChangingCounters() {
      var bucket = await _fx.AddBucketAsync(Ct, quotaBytes: 10, quotaObjects: 2, maxObjectBytes: 8);
      var store = _fx.NewStore();
      await store.PutAsync(bucket, "a", ObjectStoreFixture.Content("123456"), ct: Ct);

      var bytes = await Assert.ThrowsAsync<ActionException>(() => store.PutAsync(bucket, "b", ObjectStoreFixture.Content("12345"), ct: Ct));
      Assert.Equal(409, bytes.StatusCode);
      var tooLarge = await Assert.ThrowsAsync<ActionException>(() => store.PutAsync(bucket, "c", new NonSeekableStream("123456789"u8.ToArray()), ct: Ct));
      Assert.Equal(413, tooLarge.StatusCode);
      await store.PutAsync(bucket, "d", ObjectStoreFixture.Content("1"), ct: Ct);
      var objects = await Assert.ThrowsAsync<ActionException>(() => store.PutAsync(bucket, "e", ObjectStoreFixture.Content("2"), ct: Ct));
      Assert.Equal(409, objects.StatusCode);
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task Replicas_AreWrittenToTwoVolumes_AndRepairReplacesALostCopy() {
      var bucket = await _fx.AddBucketAsync(Ct, replicaMin: 2, replicaTarget: 2, volumes: [_fx.VolumeA, _fx.VolumeB]);
      var store = _fx.NewStore();
      var info = await store.PutAsync(bucket, "r", ObjectStoreFixture.Content("replicated"), ct: Ct);
      var pathB = _fx.BlobPath("B", info.Hash!);
      Assert.True(File.Exists(_fx.BlobPath("A", info.Hash!)));
      Assert.True(File.Exists(pathB));

      File.SetAttributes(pathB, FileAttributes.Normal);
      File.Delete(pathB);
      var summary = await _fx.NewMaintenance().ReplicateAsync(true, null, Ct);
      Assert.Contains("1 copies written", summary);
      Assert.True(File.Exists(pathB));
      var db = _fx.NewContext();
      var hash = StoreNames.ParseHash(info.Hash);
      Assert.Equal(2, await db.Copies.CountAsync(c => c.cStoreObjectBlobHash == hash && c.cStoreObjectBlobVolState == 2, Ct));
   }

   [Fact]
   public async Task Read_MarksMissingCopy_AndFallsBackToAnotherVolume() {
      var bucket = await _fx.AddBucketAsync(Ct, replicaMin: 2, replicaTarget: 2, volumes: [_fx.VolumeA, _fx.VolumeB]);
      var store = _fx.NewStore();
      var info = await store.PutAsync(bucket, "f", ObjectStoreFixture.Content("fallback"), ct: Ct);
      var pathA = _fx.BlobPath("A", info.Hash!);
      File.SetAttributes(pathA, FileAttributes.Normal);
      File.Delete(pathA);

      Assert.Equal("fallback", await ObjectStoreFixture.ReadAsync((await store.OpenAsync(bucket, "f", ct: Ct)).Content, Ct));
      var db = _fx.NewContext();
      var hash = StoreNames.ParseHash(info.Hash);
      Assert.Equal((int)StoreBlobCopyState.Missing,
         (await db.Copies.SingleAsync(c => c.cStoreObjectBlobHash == hash && c.cStoreVolumeId == _fx.VolumeA, Ct)).cStoreObjectBlobVolState);
   }

   [Fact]
   public async Task GarbageCollection_WaitsForGracePeriod_ThenDeletesFilesAndRows() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var store = _fx.NewStore();
      var info = await store.PutAsync(bucket, "gc", ObjectStoreFixture.Content("collect me " + Guid.NewGuid()), ct: Ct);
      await store.PurgeVersionAsync(bucket, "gc", 1, Ct);
      var hash = StoreNames.ParseHash(info.Hash);
      var maintenance = _fx.NewMaintenance();

      Assert.Equal(0, (await maintenance.ReviewGarbageAsync(1, Ct)).Sample.Count(b => b.Hash == info.Hash));
      await maintenance.CollectGarbageAsync(1, null, Ct);
      Assert.True(File.Exists(_fx.BlobPath("A", info.Hash!)));

      var db = _fx.NewContext();
      await db.Blobs.Where(b => b.cStoreObjectBlobHash == hash)
         .ExecuteUpdateAsync(s => s.SetProperty(b => b.cStoreObjectBlobOrphanAt, DateTime.UtcNow.AddHours(-2)), Ct);
      Assert.Contains((await maintenance.ReviewGarbageAsync(1, Ct)).Sample, b => b.Hash == info.Hash);
      await _fx.NewMaintenance().CollectGarbageAsync(1, null, Ct);
      Assert.False(File.Exists(_fx.BlobPath("A", info.Hash!)));
      Assert.False(await db.Blobs.AnyAsync(b => b.cStoreObjectBlobHash == hash, Ct));
      Assert.False(await db.Copies.AnyAsync(c => c.cStoreObjectBlobHash == hash, Ct));
   }

   [Fact]
   public async Task ParallelUploads_ToSameKey_GetDistinctVersions() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var uploads = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
         _fx.NewStore().PutAsync(bucket, "same", ObjectStoreFixture.Content("parallel " + i), ct: Ct), Ct));
      var results = await Task.WhenAll(uploads);
      Assert.Equal(Enumerable.Range(1, 8), results.Select(r => r.Version).Order());
      Assert.Equal(8, (await _fx.NewStore().VersionsAsync(bucket, "same", Ct)).Length);
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task InterruptedUpload_LeavesOnlyStagingAndAnOrphan_ThatCleanupRemoves() {
      var bucket = await _fx.AddBucketAsync(Ct);
      // Simulated crash: a staged file and a blob row that no version refers to.
      Directory.CreateDirectory(Path.Combine(_fx.Folder, "A", ".staging"));
      await File.WriteAllTextAsync(Path.Combine(_fx.Folder, "A", ".staging", Ulid.NewUlid().ToString()), "partial", Ct);
      var hash = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());
      var db = _fx.NewContext();
      db.Blobs.Add(new ta_StoreObjectBlob {
         cStoreObjectBlobHash = hash, cStoreObjectBlobCrc32 = new byte[4], cStoreObjectBlobSize = 7,
         cStoreObjectBlobState = (int)StoreBlobState.Orphan, cStoreObjectBlobOrphanAt = DateTime.UtcNow.AddHours(-3), datestamp = DateTime.UtcNow
      });
      await db.SaveChangesAsync(Ct);

      Assert.True(await _fx.NewMaintenance().CleanStagingAsync(Ct) >= 1);
      Assert.Empty(Directory.EnumerateFiles(Path.Combine(_fx.Folder, "A", ".staging")));
      await _fx.NewMaintenance().CollectGarbageAsync(1, null, Ct);
      Assert.False(await _fx.NewContext().Blobs.AnyAsync(b => b.cStoreObjectBlobHash == hash, Ct));
      Assert.Empty((await _fx.NewStore().ListAsync(bucket, ct: Ct)).Objects);
   }

   [Fact]
   public async Task Listing_GroupsByDelimiter_AndPagesWithContinuation() {
      var bucket = await _fx.AddBucketAsync(Ct);
      var store = _fx.NewStore();
      foreach (var key in new[] { "a.txt", "b/1", "b/2", "b/3", "c/1", "d.txt", "e.txt" })
         await store.PutAsync(bucket, key, ObjectStoreFixture.Content(key), ct: Ct);

      var page1 = await store.ListAsync(bucket, null, "/", null, 3, Ct);
      Assert.Equal(["a.txt"], page1.Objects.Select(o => o.Key));
      Assert.Equal(["b/", "c/"], page1.CommonPrefixes);
      Assert.NotNull(page1.NextContinuation);
      var page2 = await store.ListAsync(bucket, null, "/", page1.NextContinuation, 3, Ct);
      Assert.Equal(["d.txt", "e.txt"], page2.Objects.Select(o => o.Key));
      Assert.Empty(page2.CommonPrefixes);
      Assert.Null(page2.NextContinuation);
      Assert.Equal(["b/1", "b/2", "b/3"], (await store.ListAsync(bucket, "b/", "/", null, 10, Ct)).Objects.Select(o => o.Key));
      Assert.Empty((await store.ListAsync(bucket, "B/", null, null, 10, Ct)).Objects); // keys are case-sensitive
   }

   [Fact]
   public async Task Retention_PurgesOldVersions_AndRemovesObjectsThatAreOnlyAMarker() {
      var bucket = await _fx.AddBucketAsync(Ct, keepVersions: 1);
      var store = _fx.NewStore();
      for (var i = 1; i <= 4; i++) await store.PutAsync(bucket, "kept", ObjectStoreFixture.Content("r" + i), ct: Ct);
      await store.PutAsync(bucket, "gone", ObjectStoreFixture.Content("g"), ct: Ct);
      await store.DeleteAsync(bucket, "gone", Ct);

      await _fx.NewMaintenance().RetentionAsync(null, Ct);
      Assert.Equal([4, 3], (await store.VersionsAsync(bucket, "kept", Ct)).Select(v => v.Version));
      // "gone" keeps its one old version, so the delete marker is not the only version left and the object stays.
      var gone = await store.VersionsAsync(bucket, "gone", Ct);
      Assert.Equal([2, 1], gone.Select(v => v.Version));
      await AssertCountersMatchAsync(bucket);
   }

   [Fact]
   public async Task Retention_WithKeepZero_RemovesDeletedObjectsEntirely() {
      var bucket = await _fx.AddBucketAsync(Ct, keepVersions: 0);
      var store = _fx.NewStore();
      await store.PutAsync(bucket, "gone", ObjectStoreFixture.Content("g0"), ct: Ct);
      await store.DeleteAsync(bucket, "gone", Ct);
      await _fx.NewMaintenance().RetentionAsync(null, Ct);
      Assert.Null(await store.GetAsync(bucket, "gone", Ct));
      await AssertCountersMatchAsync(bucket);
   }

   private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
   {
      public override bool CanSeek => false;
      public override long Length => throw new NotSupportedException();
   }
}
