using System.Text;
using Em.Api.Core.Models;
using Em.Api.Core.ObjectStorage;
using Em.Api.Core.ObjectStorage.Volumes;
using Em.Shared;

namespace Em.Api.Core.Tests.ObjectStorage;

public class StoreNamesTests
{
   [Theory]
   [InlineData("abc")]
   [InlineData("my-bucket.2026")]
   [InlineData("0-9")]
   public void Bucket_AcceptsS3Names(string name) {
      Assert.Equal(name, StoreNames.Bucket(name));
   }

   [Theory]
   [InlineData("")]
   [InlineData("ab")]
   [InlineData("My-Bucket")]
   [InlineData("-bucket")]
   [InlineData("bucket-")]
   [InlineData("buck..et")]
   [InlineData("192.168.1.10")]
   [InlineData("xn--bucket")]
   [InlineData("bucket-s3alias")]
   [InlineData("bucket_name")]
   public void Bucket_RejectsInvalidNames(string name) {
      var ex = Assert.Throws<ActionException>(() => StoreNames.Bucket(name));
      Assert.Equal(400, ex.StatusCode);
   }

   [Fact]
   public void Bucket_RejectsMoreThan63Characters() {
      Assert.Throws<ActionException>(() => StoreNames.Bucket(new string('a', 64)));
      Assert.Equal(new string('a', 63), StoreNames.Bucket(new string('a', 63)));
   }

   [Theory]
   [InlineData("a")]
   [InlineData("photos/2026/Holiday.JPG")]
   [InlineData("folder/")]
   [InlineData("spaced name/ünïcode ✓")]
   [InlineData("a..b/c.d")]
   public void Key_AcceptsValidKeys(string key) {
      Assert.Equal(key, StoreNames.Key(key));
   }

   [Theory]
   [InlineData("")]
   [InlineData("a/../b")]
   [InlineData("./a")]
   [InlineData("a/.")]
   [InlineData("tab\there")]
   [InlineData("line\nbreak")]
   public void Key_RejectsInvalidKeys(string key) {
      Assert.Throws<ActionException>(() => StoreNames.Key(key));
   }

   [Fact]
   public void Key_LimitIsCountedInUtf8Bytes() {
      Assert.Equal(1024, StoreNames.Key(new string('a', 1024)).Length);
      Assert.Throws<ActionException>(() => StoreNames.Key(new string('a', 1025)));
      // 'é' is two UTF-8 bytes: 513 of them exceed 1024 bytes although they are only 513 characters.
      Assert.Throws<ActionException>(() => StoreNames.Key(new string('é', 513)));
   }

   [Fact]
   public void Key_RejectsLoneSurrogate() {
      Assert.Throws<ActionException>(() => StoreNames.Key("a\uD800b"));
   }

   [Fact]
   public void KeyHash_IsCaseSensitiveSha256() {
      var lower = StoreNames.KeyHash("photo.jpg");
      Assert.Equal(32, lower.Length);
      Assert.Equal(lower, StoreNames.KeyHash("photo.jpg"));
      Assert.NotEqual(lower, StoreNames.KeyHash("Photo.jpg"));
      Assert.Equal(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("photo.jpg")), lower);
   }

   [Fact]
   public void Mime_DefaultsAndValidates() {
      Assert.Equal(StoreNames.DefaultMime, StoreNames.Mime(null));
      Assert.Equal(StoreNames.DefaultMime, StoreNames.Mime(" "));
      Assert.Equal("text/plain; charset=utf-8", StoreNames.Mime("text/plain; charset=utf-8"));
      Assert.Throws<ActionException>(() => StoreNames.Mime("not a mime"));
      Assert.Throws<ActionException>(() => StoreNames.Mime("text/plain\r\nX-Injected: 1"));
   }

   [Fact]
   public void Metadata_SerializesSortedAndRoundTrips() {
      var json = StoreNames.Metadata(new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
      Assert.Equal("{\"a\":\"1\",\"b\":\"2\"}", json);
      Assert.Equal("1", StoreNames.ReadMetadata(json)!["a"]);
      Assert.Null(StoreNames.Metadata(null));
      Assert.Null(StoreNames.ReadMetadata("not json"));
   }

   [Fact]
   public void Metadata_RejectsTooManyOrInvalidEntries() {
      var many = Enumerable.Range(0, StoreNames.MaxMetadataEntries + 1).ToDictionary(i => $"k{i}", i => "v");
      Assert.Throws<ActionException>(() => StoreNames.Metadata(many));
      Assert.Throws<ActionException>(() => StoreNames.Metadata(new Dictionary<string, string> { ["bad key"] = "v" }));
      Assert.Throws<ActionException>(() => StoreNames.Metadata(new Dictionary<string, string> { ["k"] = new string('x', 1025) }));
   }

   [Theory]
   [InlineData("photos/2026/")]
   [InlineData("ü/￿")]
   [InlineData("")]
   public void Continuation_RoundTrips(string after) {
      var token = StoreNames.EncodeContinuation(after);
      Assert.DoesNotContain('+', token);
      Assert.DoesNotContain('/', token);
      Assert.Equal(after, StoreNames.DecodeContinuation(token));
   }

   [Fact]
   public void Continuation_RejectsForeignTokens() {
      Assert.Null(StoreNames.DecodeContinuation(null));
      Assert.Throws<ActionException>(() => StoreNames.DecodeContinuation("%%%"));
      Assert.Throws<ActionException>(() => StoreNames.DecodeContinuation(Convert.ToBase64String(Encoding.UTF8.GetBytes("offset:10"))));
   }
}

public class StoreCrc32Tests
{
   [Fact]
   public void Compute_MatchesStandardCheckValue() {
      // The CRC-32/ISO-HDLC check value of "123456789".
      Assert.Equal(new byte[] { 0xCB, 0xF4, 0x39, 0x26 }, StoreCrc32.Compute(Encoding.ASCII.GetBytes("123456789")));
   }

   [Fact]
   public void Append_IsIncremental() {
      var crc = new StoreCrc32();
      crc.Append(Encoding.ASCII.GetBytes("1234"));
      crc.Append(Encoding.ASCII.GetBytes("56789"));
      Assert.Equal(0xCBF43926u, crc.Value);
   }
}

public class StoreHashLockTests
{
   [Fact]
   public async Task SameHash_IsSerialized_OtherHash_IsNot() {
      var locks = new StoreHashLock();
      var a = new byte[32];
      var b = new byte[32];
      b[0] = 1;
      var first = await locks.EnterAsync(a, TestContext.Current.CancellationToken);
      var second = locks.EnterAsync(a, TestContext.Current.CancellationToken);
      var other = await locks.EnterAsync(b, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.False(second.IsCompleted);
      other.Dispose();
      first.Dispose();
      (await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();
      Assert.Equal(0, locks.ActiveCount);
   }

   [Fact]
   public async Task CancelledWait_LeavesNoEntry() {
      var locks = new StoreHashLock();
      var hash = new byte[32];
      using (await locks.EnterAsync(hash, TestContext.Current.CancellationToken)) {
         using var cts = new CancellationTokenSource(50);
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.EnterAsync(hash, cts.Token));
      }
      Assert.Equal(0, locks.ActiveCount);
   }
}

public class LocalFolderVolumeTests : IDisposable
{
   private readonly string _root = Path.Combine(Path.GetTempPath(), "em-objectstore-" + Guid.NewGuid().ToString("N"));

   public void Dispose() {
      if (!Directory.Exists(_root)) return;
      foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
      Directory.Delete(_root, true);
   }

   private static byte[] Hash(string text) => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));

   [Fact]
   public void BlobPath_FansOutByHashPrefix() {
      var volume = (LocalFolderVolume)new LocalFolderVolumeProvider(_root).Open("v1", "data");
      var hash = Hash("x");
      var hex = Convert.ToHexStringLower(hash);
      Assert.Equal(Path.Combine(_root, "data", "blobs", hex[..2], hex[2..4], hex), volume.BlobPath(hash));
   }

   [Fact]
   public void Resolve_RejectsDriveRoot() {
      var provider = new LocalFolderVolumeProvider(_root);
      Assert.Throws<ActionException>(() => provider.Resolve(Path.GetPathRoot(_root)!));
      Assert.False(provider.Validate("").IsValid);
   }

   [Fact]
   public async Task Staging_CommitsIntoPlace_AndDisposeWithoutCommitLeavesNothing() {
      var volume = new LocalFolderVolumeProvider(_root).Open("v1", "vol");
      Assert.True(volume.Probe().IsValid);
      var content = Encoding.UTF8.GetBytes("hello object storage");
      var hash = Hash("hello object storage");

      await using (var staging = volume.BeginWrite()) {
         await staging.Stream.WriteAsync(content, TestContext.Current.CancellationToken);
         await using (var read = staging.OpenRead()) Assert.Equal(content.Length, read.Length);
         await staging.CommitAsync(hash, content.Length, TestContext.Current.CancellationToken);
      }
      Assert.True(volume.TryGetLength(hash, out var length));
      Assert.Equal(content.Length, length);
      await using (var stream = volume.OpenRead(hash)!) {
         using var copy = new MemoryStream();
         await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
         Assert.Equal(content, copy.ToArray());
      }

      await using (var abandoned = volume.BeginWrite()) await abandoned.Stream.WriteAsync(content, TestContext.Current.CancellationToken);
      Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "vol", ".staging")));

      Assert.True(volume.Delete(hash));
      Assert.False(volume.Delete(hash));
      Assert.Null(volume.OpenRead(hash));
   }

   [Fact]
   public async Task Commit_RejectsUnexpectedLength() {
      var volume = new LocalFolderVolumeProvider(_root).Open("v1", "vol");
      await using var staging = volume.BeginWrite();
      await staging.Stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
      await Assert.ThrowsAsync<IOException>(() => staging.CommitAsync(Hash("x"), 4, TestContext.Current.CancellationToken));
   }

   [Fact]
   public void CleanStaging_RemovesLeftovers() {
      var volume = new LocalFolderVolumeProvider(_root).Open("v1", "vol");
      Directory.CreateDirectory(Path.Combine(_root, "vol", ".staging"));
      File.WriteAllText(Path.Combine(_root, "vol", ".staging", "leftover"), "x");
      Assert.Equal(1, volume.CleanStaging());
      Assert.Equal(0, volume.CleanStaging());
   }

   [Fact]
   public async Task WriteCopy_VerifiesSizeAndCrc() {
      var volume = new LocalFolderVolumeProvider(_root).Open("v2", "copy");
      var content = Encoding.UTF8.GetBytes("replica");
      var hash = Hash("replica");
      var crc = StoreCrc32.Compute(content);
      await ObjectStore.WriteCopyAsync(volume, hash, new MemoryStream(content), content.Length, crc, TestContext.Current.CancellationToken);
      Assert.True(volume.TryGetLength(hash, out _));

      var damaged = Encoding.UTF8.GetBytes("replicA");
      await Assert.ThrowsAsync<StoreCopyMismatchException>(() =>
         ObjectStore.WriteCopyAsync(volume, Hash("other"), new MemoryStream(damaged), damaged.Length, crc, TestContext.Current.CancellationToken));
      Assert.False(volume.TryGetLength(Hash("other"), out _));
   }
}

public class ObjectStorageRulesTests
{
   private static StorePolicySave Policy(Action<StorePolicySave>? change = null) {
      var p = new StorePolicySave { Name = "p", ReplicaMin = 1, ReplicaTarget = 2, VolumeIds = ["a", "b"] };
      change?.Invoke(p);
      return p;
   }

   [Fact]
   public void ValidatePolicy_AcceptsDefaults() => ObjectStorageServices.ValidatePolicy(Policy());

   [Theory]
   [InlineData(0, 1)]
   [InlineData(2, 1)]
   [InlineData(1, 17)]
   public void ValidatePolicy_RejectsReplicaRules(int min, int target) {
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => {
         p.ReplicaMin = min;
         p.ReplicaTarget = target;
         p.VolumeIds = Enumerable.Range(0, 20).Select(i => $"v{i}").ToArray();
      })));
   }

   [Fact]
   public void ValidatePolicy_NeedsEnoughDistinctVolumes() {
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => p.VolumeIds = ["a"])));
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => p.VolumeIds = ["a", "a"])));
   }

   [Fact]
   public void ValidatePolicy_RejectsNegativeLimits() {
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => p.QuotaBytes = -1)));
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => p.MaxObjectBytes = 0)));
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => p.KeepDays = 0)));
      Assert.Throws<ActionException>(() => ObjectStorageServices.ValidatePolicy(Policy(p => p.Access = (StoreAccess)9)));
   }

   private static readonly DateTime Now = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

   private static (int, DateTime)[] History(int count) =>
      Enumerable.Range(1, count).Select(v => (v, Now.AddDays(v - count))).ToArray();

   [Fact]
   public void ExpiredVersions_KeepsNewestNonCurrentVersions() {
      // Versions 1..5, current 5: non-current newest first are 4,3,2,1; keep two.
      Assert.Equal([2, 1], ObjectStorageMaintenance.ExpiredVersions(History(5), 5, 2, null));
      Assert.Equal([4, 3, 2, 1], ObjectStorageMaintenance.ExpiredVersions(History(5), 5, 0, null));
      Assert.Empty(ObjectStorageMaintenance.ExpiredVersions(History(5), 5, null, null));
   }

   [Fact]
   public void ExpiredVersions_NeverPurgesCurrent_EvenWhenOld() {
      // Current is version 3 (an older version made current by a purge): non-current newest first are 5, 4, 2, 1.
      Assert.Equal([4, 2, 1], ObjectStorageMaintenance.ExpiredVersions(History(5), 3, 1, null));
      Assert.Equal([5, 4, 2, 1], ObjectStorageMaintenance.ExpiredVersions(History(5), 3, 0, Now.AddYears(1)));
   }

   [Fact]
   public void ExpiredVersions_AppliesAgeCutoff() {
      // Created: v1 = -4 days ... v5 = today. Cutoff two days ago removes v1 and v2.
      Assert.Equal([2, 1], ObjectStorageMaintenance.ExpiredVersions(History(5), 5, null, Now.AddDays(-2).AddHours(-1)));
   }
}
