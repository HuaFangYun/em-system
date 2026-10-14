using Em.Api.Core.Models;
using Em.Api.Core.ObjectStorage;
using Em.Api.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.Tests.ObjectStorage;

public class ManagedBucketTests
{
   private const string Description = "Attachments written by the sample module; quota changes affect uploads.";

   private static EmAppBuilder NewBuilder() => new() { Services = new ServiceCollection() };

   [Fact]
   public void Declaration_IsValidatedAndTrimmed() {
      var builder = NewBuilder();
      builder.AddManagedBucket("app-files", "  Sample App  ", "  " + Description + "  ", o => o.KeepVersions = 3);
      var declaration = Assert.Single(Registry(builder).Items);
      Assert.Equal("Sample App", declaration.Owner);
      Assert.Equal(Description, declaration.Description);
      Assert.Equal(3, declaration.Defaults.KeepVersions);
      Assert.Equal(StoreAccess.Private, declaration.Defaults.Access);
   }

   [Theory]
   [InlineData("Bad_Name", "Owner", Description)]
   [InlineData("ab", "Owner", Description)]
   [InlineData("app-files", "", Description)]
   [InlineData("app-files", "   ", Description)]
   [InlineData("app-files", "Owner", "")]
   [InlineData("app-files", "Owner", "too short")]
   public void Declaration_RejectsInvalidNameOwnerOrDescription(string name, string owner, string description) {
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket(name, owner, description));
   }

   [Fact]
   public void Declaration_RejectsTooLongOwnerAndDescription() {
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", new string('o', 101), Description));
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", "Owner", new string('d', 501)));
      NewBuilder().AddManagedBucket("app-files", new string('o', 100), new string('d', 500));
   }

   [Fact]
   public void Declaration_RejectsInvalidDefaults() {
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", "Owner", Description, o => o.KeepDays = 0));
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", "Owner", Description, o => o.QuotaBytes = -1));
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", "Owner", Description, o => o.MaxObjectBytes = 0));
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", "Owner", Description, o => o.Access = (StoreAccess)9));
      Assert.Throws<ArgumentException>(() => NewBuilder().AddManagedBucket("app-files", "Owner", Description, o => o.Note = new string('n', 501)));
   }

   [Fact]
   public void Declaration_TwiceThrows() {
      var builder = NewBuilder();
      builder.AddManagedBucket("app-files", "Owner", Description);
      var error = Assert.Throws<InvalidOperationException>(() => builder.AddManagedBucket("app-files", "Other", Description));
      Assert.Contains("already declared by Owner", error.Message);
   }

   [Fact]
   public void Declaration_BeforeOrAfterAddObjectStorage_ReachesTheRuntime() {
      var builder = NewBuilder();
      builder.AddManagedBucket("first-files", "Owner", Description);
      builder.AddObjectStorage(o => o.DefaultVolumePath = "storage/managed");
      builder.AddManagedBucket("second-files", "Owner", Description);
      var options = (ObjectStorageOptions)builder.Services.Single(d => d.ServiceType == typeof(ObjectStorageOptions)).ImplementationInstance!;
      Assert.Equal("storage/managed", options.DefaultVolumePath);
      Assert.Equal(["first-files", "second-files"], Registry(builder).Items.Select(d => d.Name));
      Assert.True(Registry(builder).IsDeclared("second-files"));
      Assert.False(Registry(builder).IsDeclared("other"));
   }

   [Fact]
   public void Options_DefaultVolumePath_FallsBackToDataFolder() {
      Assert.Equal("data/objectstore", new ObjectStorageOptions().DefaultVolumePath);
      var builder = NewBuilder();
      builder.AddObjectStorage(o => o.DefaultVolumePath = " ");
      var options = (ObjectStorageOptions)builder.Services.Single(d => d.ServiceType == typeof(ObjectStorageOptions)).ImplementationInstance!;
      Assert.Equal(ObjectStorageOptions.DefaultVolumeFallback, options.DefaultVolumePath);
   }

   [Fact]
   public void PolicyDraft_UsesDefaultsOnOneVolume() {
      var declaration = ManagedBucketDeclaration.Create("app-files", "Owner", Description,
         new ManagedBucketOptions { Access = StoreAccess.PublicRead, KeepDays = 30, QuotaObjects = 10 });
      var draft = declaration.PolicyDraft("app-files (managed)", "vol");
      Assert.Equal(StoreAccess.PublicRead, draft.Access);
      Assert.Equal(30, draft.KeepDays);
      Assert.Equal(10, draft.QuotaObjects);
      Assert.Equal(["vol"], draft.VolumeIds);
      Assert.Equal((1, 1), (draft.ReplicaMin, draft.ReplicaTarget));
      Assert.Contains("app-files", draft.Note);
   }

   [Fact]
   public void Json_ReadsWritesAndKeepsOtherKeys() {
      Assert.Equal((null, null), ManagedBucketJson.Read(null));
      Assert.Equal((null, null), ManagedBucketJson.Read("not json"));
      Assert.Equal((null, null), ManagedBucketJson.Read("[1]"));

      var json = ManagedBucketJson.Write("""{"other":1}""", "Owner", "What it holds");
      Assert.Equal(("Owner", "What it holds"), ManagedBucketJson.Read(json));
      Assert.Contains("\"other\":1", json);

      var released = ManagedBucketJson.Write(json, null, "What it holds");
      Assert.Equal((null, "What it holds"), ManagedBucketJson.Read(released));
      Assert.Null(ManagedBucketJson.Owner(released));
      Assert.Null(ManagedBucketJson.Write(ManagedBucketJson.Write(null, "Owner", null), null, null));
   }

   private static ManagedBucketRegistry Registry(EmAppBuilder builder) => builder.ManagedBuckets;
}
