using Em.Api.Core.Models;
using Em.Ui.Wpf.Core;
using Em.Ui.Wpf.Shared;

namespace Em.Ui.Wpf.Navigations;

public sealed partial class ObjectStorageManagerVm
{
   /// <summary>
   /// The view model serves the bucket editor (<see cref="StoreBucketEditor"/>): one bucket, its settings always
   /// shown next to its objects, instead of the whole manager. Set by <see cref="LoadBucketPageAsync"/>.
   /// </summary>
   public bool IsBucketPage { get => Get<bool>(); private set => Set(value); }

   /// <summary>The bucket page holds a bucket that is not saved yet, so it has no objects to show.</summary>
   public bool IsBucketPageNew => IsBucketPage && IsNewBucket;

   private string _pagePrefix = "";

   /// <summary>
   /// Turns this view model into the bucket editor of <paramref name="payload"/>: loads the policies, then the
   /// bucket by name with its settings in the draft fields and its objects at the payload's folder, or an empty
   /// draft for a new bucket. An unknown bucket name leaves the page empty with a message in <see cref="Status"/>.
   /// </summary>
   /// <param name="payload">Bucket to open.</param>
   public Task LoadBucketPageAsync(StoreBucketNavigationPayload payload) {
      IsBucketPage = true;
      _pagePrefix = payload.Prefix;
      _bucketEditId = null;
      IsNewBucket = payload.BucketName is null;
      BucketDraftName = payload.BucketName ?? "";
      return ReloadAsync();
   }

   /// <summary>Opens the selected bucket in its own bucket editor tab.</summary>
   public async Task OpenBucketPageCommand() {
      if (!CanActOnBucket || SelectedBucket is not { } bucket || NavigationEntry is not { } entry) return;
      await entry.NavigateTo(EmApp.ObjectStorageBucketNavigationName, StoreBucketNavigationPayload.Open(bucket.Name, IsBrowsing ? Prefix : null));
   }

   /// <summary>Opens the bucket editor for a new bucket in its own tab.</summary>
   public async Task NewBucketPageCommand() {
      if (!CanBrowse || NavigationEntry is not { } entry) return;
      await entry.NavigateTo(EmApp.ObjectStorageBucketNavigationName, StoreBucketNavigationPayload.New());
   }

   // Body of ReloadAsync on the bucket page: the bucket is looked up again by ID once known, by name before.
   private async Task LoadBucketPageCoreAsync() {
      await LoadSettingsAsync();
      await LoadBucketsAsync();
      if (IsNewBucket) {
         // New bucket: the same defaults as the side sheet of the manager; the typed draft survives a reload.
         IsBrowsing = false;
         OpenBucket = null;
         if (BucketDraftPolicy is null || Policies.All(p => p.Id != BucketDraftPolicy.Id))
            BucketDraftPolicy = Policies.FirstOrDefault(p => p.State == StorePolicyState.Active);
         if (!IsLoaded) BucketDraftState = StoreBucketState.Active;
         NotifyChanged(nameof(IsBucketPageNew));
         IsLoaded = true;
         Status = Policies.Count == 0 ? "Create a policy in Object Storage Manager before creating a bucket." : "Fill in the bucket and save it to start adding objects.";
         return;
      }
      var bucket = _bucketEditId is { } id ? Buckets.FirstOrDefault(b => b.Id == id) : Buckets.FirstOrDefault(b => b.Name == BucketDraftName);
      if (bucket is null) {
         IsBrowsing = false;
         OpenBucket = null;
         throw new Em.Shared.ActionException($"Bucket '{BucketDraftName}' does not exist (any more).", 404);
      }

      _bucketEditId = bucket.Id;
      IsNewBucket = false;
      NotifyChanged(nameof(IsBucketPageNew));
      BucketDraftName = bucket.Name;
      BucketDraftPolicy = Policies.FirstOrDefault(p => p.Id == bucket.PolicyId);
      BucketDraftState = bucket.State;
      BucketDraftNote = bucket.Note;
      SelectedBucket = bucket;
      var reopen = IsBrowsing && OpenBucket?.Id == bucket.Id;
      OpenBucket = bucket;
      if (!reopen) Prefix = _pagePrefix;
      IsBrowsing = true;
      IsLoaded = true;
      await LoadObjectsAsync(false);
   }

   // After a save on the bucket page: a new bucket becomes the open one and its tab takes its name.
   private async Task AfterBucketPageSaveAsync(StoreBucketInfo saved) {
      var wasNew = IsNewBucket;
      _bucketEditId = saved.Id;
      IsNewBucket = false;
      if (wasNew) _pagePrefix = "";
      await LoadBucketPageCoreAsync();
      if (wasNew) NavigationEntry?.SetTitle(StoreBucketNavigationPayload.TitleOf(saved.Name));
   }
}
