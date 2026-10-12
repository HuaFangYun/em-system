using System.IO;
using System.Runtime.CompilerServices;
using Em.Api.Core.Models;
using Em.Shared;
using Em.Ui.Wpf.Navigations;
using Em.Ui.Wpf.Shared;

namespace Em.Ui.Wpf.Core.Tests;

public class ObjectStorageManagerTests
{
   // Loads XAML resources only: no window, layout render, screenshot or live application.
   [WpfFact]
   public void ManagerXamlLoadsWithoutRendering() {
      var app = (EmApp)RuntimeHelpers.GetUninitializedObject(typeof(EmApp));
      var view = new ObjectStorageManager(app);
      Assert.IsType<System.Windows.Style>(view.Resources["storeRowStyle"]);
      Assert.IsType<System.Windows.Controls.ListBox>(view.FindName("objectList"));
      Assert.True(view.Vm.IsBucketsTab);
      Assert.False(view.Vm.CanBrowse);
   }

   [Fact]
   public async Task ReloadLoadsEverythingAndBrowsesFolders() {
      var service = new StubService();
      var vm = new ObjectStorageManagerVm(service);
      await vm.ReloadAsync();
      Assert.True(vm.CanBrowse);
      Assert.Single(vm.Buckets);
      Assert.Equal(2, vm.Volumes.Count);
      Assert.Single(vm.Policies);
      Assert.Contains(vm.StatusLines, l => l.Caption == "Buckets" && l.Value == "1");

      vm.SelectedBucket = vm.Buckets[0];
      await vm.OpenBucketCommand();
      Assert.True(vm.IsBrowsing);
      Assert.Equal(["photos", "readme.txt"], vm.Items.Select(i => i.Name));
      Assert.True(vm.Items[0].IsFolder);
      Assert.True(vm.HasMore);
      Assert.Equal([""], vm.Crumbs.Select(c => c.Prefix));

      vm.SelectedItem = vm.Items[0];
      await vm.OpenItemCommand();
      Assert.Equal("photos/", vm.Prefix);
      Assert.Equal(["", "photos/"], vm.Crumbs.Select(c => c.Prefix));
      Assert.Equal("photos/", service.LastPrefix);

      await vm.NavigateCrumbCommand(vm.Crumbs[0]);
      Assert.Equal("", vm.Prefix);
      await vm.CloseBrowserCommand();
      Assert.False(vm.IsBrowsing);
   }

   [Fact]
   public async Task PolicyDraftConvertsMegabytesAndKeepsVolumeOrder() {
      var service = new StubService();
      var vm = new ObjectStorageManagerVm(service);
      await vm.ReloadAsync();
      vm.NewPolicyCommand();
      vm.PolicyName = " archive ";
      vm.PolicyQuotaMb = "1.5";
      vm.PolicyMaxObjectMb = "";
      vm.PolicyKeepVersions = "3";
      vm.PolicyVolumes[0].IsSelected = true;
      vm.PolicyVolumes[1].IsSelected = true;
      vm.SelectedPolicyVolume = vm.PolicyVolumes[1];
      vm.MoveVolumeUpCommand();

      var draft = vm.BuildPolicyDraft();
      Assert.Equal("archive", draft.Name);
      Assert.Equal(1_572_864, draft.QuotaBytes);
      Assert.Null(draft.MaxObjectBytes);
      Assert.Equal(3, draft.KeepVersions);
      Assert.Equal(["vol-b", "vol-a"], draft.VolumeIds);

      vm.PolicyKeepDays = "soon";
      await vm.SavePolicyCommand();
      Assert.Null(service.LastPolicySave);
      Assert.Contains("Kept days", vm.Status);
   }

   [Fact]
   public async Task GarbageCollectionNeedsMatchingReviewAndConfirmation() {
      var service = new StubService();
      var vm = new ObjectStorageManagerVm(service) { Confirm = (_, _) => false };
      await vm.ReloadAsync();
      Assert.False(vm.Commands[nameof(vm.RunGcCommand)]!.CanExecute());
      await vm.ReviewGcCommand();
      Assert.True(vm.Commands[nameof(vm.RunGcCommand)]!.CanExecute());
      await vm.RunGcCommand();
      Assert.Equal(0, service.GcRuns);

      vm.GcGraceHours = 48;
      Assert.Null(vm.GcReview);
      Assert.False(vm.Commands[nameof(vm.RunGcCommand)]!.CanExecute());
      await vm.ReviewGcCommand();
      vm.Confirm = (_, _) => true;
      await vm.RunGcCommand();
      Assert.Equal(1, service.GcRuns);
      Assert.Equal(48, service.LastGrace);
   }

   [Fact]
   public async Task DeleteBucketAsksFirstAndServerRejectionShowsInStatus() {
      var service = new StubService();
      var asked = 0;
      var vm = new ObjectStorageManagerVm(service) { Confirm = (_, _) => ++asked > 1 };
      await vm.ReloadAsync();
      vm.SelectedBucket = vm.Buckets[0];
      await vm.DeleteBucketCommand();
      Assert.Equal(0, service.BucketDeletes);
      await vm.DeleteBucketCommand();
      Assert.Equal(1, service.BucketDeletes);
      Assert.Contains("still holds objects", vm.Status);
      Assert.True(vm.IsNotBusy);
   }

   [WpfFact]
   public void BucketEditorXamlLoadsWithoutRendering() {
      var app = (EmApp)RuntimeHelpers.GetUninitializedObject(typeof(EmApp));
      var view = new StoreBucketEditor(app);
      Assert.IsType<System.Windows.Controls.ListBox>(view.FindName("objectList"));
      Assert.False(view.Vm.IsBucketPage);
   }

   [Fact]
   public async Task BucketPageOpensBucketByNameAtItsFolder() {
      var service = new StubService();
      var vm = new ObjectStorageManagerVm(service);
      await vm.LoadBucketPageAsync(StoreBucketNavigationPayload.Open("files", "photos"));
      Assert.True(vm.IsBucketPage);
      Assert.True(vm.IsBrowsing);
      Assert.False(vm.IsNewBucket);
      Assert.Equal("b1", vm.OpenBucket?.Id);
      Assert.Equal("photos/", vm.Prefix);
      Assert.Equal("photos/", service.LastPrefix);
      Assert.Equal("Default", vm.BucketDraftPolicy?.Name);
      Assert.True(vm.Commands[nameof(vm.SaveBucketCommand)]!.CanExecute());
      Assert.True(vm.Commands[nameof(vm.UploadCommand)]!.CanExecute());

      await vm.RefreshCommand();
      Assert.Equal("photos/", vm.Prefix);
   }

   [Fact]
   public async Task BucketPageReportsUnknownBucketAndStartsNewOnes() {
      var missing = new ObjectStorageManagerVm(new StubService());
      await missing.LoadBucketPageAsync(StoreBucketNavigationPayload.Open("nope"));
      Assert.False(missing.IsBrowsing);
      Assert.Contains("does not exist", missing.Status);
      Assert.False(missing.Commands[nameof(missing.SaveBucketCommand)]!.CanExecute());

      var created = new ObjectStorageManagerVm(new StubService());
      await created.LoadBucketPageAsync(StoreBucketNavigationPayload.New());
      Assert.True(created.IsBucketPageNew);
      Assert.False(created.IsBrowsing);
      Assert.Equal("", created.BucketDraftName);
      Assert.Equal("pol", created.BucketDraftPolicy?.Id);
      Assert.True(created.Commands[nameof(created.SaveBucketCommand)]!.CanExecute());
   }

   [Fact]
   public void BucketPayloadNormalizesPrefixAndTitle() {
      Assert.Equal("a/b/", StoreBucketNavigationPayload.Open("files", "/a/b").Prefix);
      Assert.Equal("", StoreBucketNavigationPayload.Open("files").Prefix);
      Assert.Equal("Bucket: files", StoreBucketNavigationPayload.Open(" files ").Title);
      Assert.Equal("New Bucket", StoreBucketNavigationPayload.New().Title);
   }

   [Fact]
   public void GuessMimeUsesExtension() {
      Assert.Equal("image/png", ObjectStorageManagerVm.GuessMime("a/b.PNG"));
      Assert.Equal("application/octet-stream", ObjectStorageManagerVm.GuessMime("noext"));
   }

   private sealed class StubService : IObjectStorageServices
   {
      public string? LastPrefix;
      public StorePolicySave? LastPolicySave;
      public int GcRuns, LastGrace, BucketDeletes;

      private static readonly StoreVolumeValidation Ok = new(true, "/srv", "OK", null, true);
      private static readonly StoreObjectInfo Readme = new("o1", "b1", "readme.txt", 1, 10, "text/plain", false, DateTime.UtcNow, "ab");

      public Task<StoreStatus> GetMeta_StoreStatus() => Task.FromResult(new StoreStatus(2, 0, 1, 1, 1, 10, 1, 10, 0, 0, 0, 0, [], null, null));
      public Task<StoreVolumeInfo[]> GetMeta_StoreVolumes() => Task.FromResult<StoreVolumeInfo[]>([
         new("vol-a", "A", StoreVolumeState.Active, StoreVolumeType.LocalFolder, "a", null, 1, 10, 0, 1, Ok),
         new("vol-b", "B", StoreVolumeState.Active, StoreVolumeType.LocalFolder, "b", null, 0, 0, 0, 0, Ok)]);
      public Task<StoreVolumeValidation> PostGetMeta_StoreVolumeValidate(StoreVolumeSave draft) => Task.FromResult(Ok);
      public Task<StoreVolumeInfo> PostGetMeta_StoreVolumeSave(StoreVolumeSave request) => throw new NotSupportedException();
      public Task PostMeta_StoreVolumeDelete(string volumeId) => Task.CompletedTask;
      public Task<StorePolicyInfo[]> GetMeta_StorePolicies() => Task.FromResult<StorePolicyInfo[]>([
         new("pol", "Default", StorePolicyState.Active, null, 1, 1, null, null, null, null, null, StoreAccess.Private, ["vol-a"], 1)]);
      public Task<StorePolicyInfo> PostGetMeta_StorePolicySave(StorePolicySave request) {
         LastPolicySave = request;
         return Task.FromResult(new StorePolicyInfo("pol2", request.Name, request.State, null, 1, 1, null, null, null, null, null, request.Access, request.VolumeIds, 0));
      }
      public Task PostMeta_StorePolicyDelete(string policyId) => Task.CompletedTask;
      public Task<StoreBucketInfo[]> GetMeta_StoreBuckets() => Task.FromResult<StoreBucketInfo[]>([
         new("b1", "files", "pol", "Default", StoreBucketState.Active, null, 10, 1, null, null, null, StoreAccess.Private, DateTime.UtcNow)]);
      public Task<StoreBucketInfo> PostGetMeta_StoreBucketSave(StoreBucketSave request) => throw new NotSupportedException();
      public Task PostMeta_StoreBucketDelete(string bucketId) {
         BucketDeletes++;
         throw new ActionException("The bucket still holds objects, including deleted objects with history. Purge them first.", 409);
      }
      public Task<StoreObjectListing> GetMeta_StoreObjects(string bucketId, string? prefix, string? delimiter, string? continuation, int limit) {
         LastPrefix = prefix;
         return Task.FromResult(prefix is null
            ? new StoreObjectListing(null, "/", ["photos/"], [Readme], "next")
            : new StoreObjectListing(prefix, "/", [], [Readme with { Key = prefix + "a.jpg" }], null));
      }
      public Task<StoreObjectInfo> GetMeta_StoreObject(string bucketId, string key) => Task.FromResult(Readme);
      public Task<StoreObjectInfo> PostGetMeta_StoreObjectUpload(StoreUploadRequest request, Stream content) => Task.FromResult(Readme);
      public Task<Stream> GetMeta_StoreObjectDownload(string bucketId, string key, int? version) => Task.FromResult<Stream>(new MemoryStream());
      public Task PostMeta_StoreObjectDelete(string bucketId, string key) => Task.CompletedTask;
      public Task<StoreObjectVersionInfo[]> GetMeta_StoreObjectVersions(string bucketId, string key) => Task.FromResult<StoreObjectVersionInfo[]>([]);
      public Task<StoreObjectInfo> PostGetMeta_StoreObjectRestore(string bucketId, string key, int version) => Task.FromResult(Readme);
      public Task PostMeta_StoreObjectVersionPurge(string bucketId, string key, int version) => Task.CompletedTask;
      public Task<StoreGcReview> GetMeta_StoreGcReview(int graceHours) => Task.FromResult(new StoreGcReview(graceHours, 0, 1, 5, 1, 5, []));
      public Task<BusinessTaskInfo> PostGetMeta_StoreGcRun(int graceHours) {
         GcRuns++;
         LastGrace = graceHours;
         return Task.FromResult(new BusinessTaskInfo { Title = "Object storage garbage collection" });
      }
      public Task<BusinessTaskInfo> PostGetMeta_StoreRetentionRun() => Task.FromResult(new BusinessTaskInfo { Title = "Retention" });
      public Task<BusinessTaskInfo> PostGetMeta_StoreRepairRun() => Task.FromResult(new BusinessTaskInfo { Title = "Repair" });
      public Task<BusinessTaskInfo[]> GetMeta_StoreTasks() => Task.FromResult<BusinessTaskInfo[]>([]);
   }
}
