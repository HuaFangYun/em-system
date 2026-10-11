using System.Collections.ObjectModel;
using System.IO;
using Em.Api.Core.Models;
using Em.Ui.Wpf.Shared;

namespace Em.Ui.Wpf.Navigations;

/// <summary>One step of the object browser path.</summary>
/// <param name="Title">Caption (bucket name or folder name).</param>
/// <param name="Prefix">Key prefix the step opens; empty for the bucket root.</param>
public sealed record StoreCrumb(string Title, string Prefix);

/// <summary>One row of the object browser: a pseudo-folder (common prefix) or an object.</summary>
/// <param name="Name">Name relative to the current prefix.</param>
/// <param name="Prefix">Common prefix of a folder; <c>null</c> for an object.</param>
/// <param name="Object">The object; <c>null</c> for a folder.</param>
public sealed record StoreBrowserItem(string Name, string? Prefix, StoreObjectInfo? Object)
{
   /// <summary>The row is a pseudo-folder.</summary>
   public bool IsFolder => Object is null;

   /// <summary>Size caption.</summary>
   public string SizeText => Object is { } o ? ObjectStorageManagerVm.FormatSize(o.Size) : "";

   /// <summary>Modification time caption in local time.</summary>
   public string ModifiedText => Object is { } o ? DateTime.SpecifyKind(o.ModifiedAt, DateTimeKind.Utc).ToLocalTime().ToString("g") : "";

   /// <summary>Version caption.</summary>
   public string VersionText => Object is { } o ? $"v{o.Version}" : "";
}

public sealed partial class ObjectStorageManagerVm
{
   private const int PageSize = 200;

   /// <summary>Buckets.</summary>
   public ObservableCollection<StoreBucketInfo> Buckets { get; } = [];

   /// <summary>Selected bucket row.</summary>
   public StoreBucketInfo? SelectedBucket { get => Get<StoreBucketInfo>(); set => Set(value, _ => UpdateState()); }

   /// <summary>A bucket row is selected and the list can be used.</summary>
   public bool CanActOnBucket => CanBrowse && SelectedBucket is not null;

   /// <summary>The object browser of <see cref="OpenBucket"/> is shown instead of the bucket table.</summary>
   public bool IsBrowsing { get => Get<bool>(); private set => Set(value, _ => { NotifyChanged(nameof(IsNotBrowsing)); UpdateState(); }); }

   /// <summary>The bucket table is shown.</summary>
   public bool IsNotBrowsing => !IsBrowsing;

   /// <summary>Bucket whose objects are browsed.</summary>
   public StoreBucketInfo? OpenBucket { get => Get<StoreBucketInfo>(); private set => Set(value); }

   /// <summary>Current key prefix of the browser, ending with <c>/</c> or empty.</summary>
   public string Prefix { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Path from the bucket root to the current prefix.</summary>
   public ObservableCollection<StoreCrumb> Crumbs { get; } = [];

   /// <summary>Folders and objects under the current prefix.</summary>
   public ObservableCollection<StoreBrowserItem> Items { get; } = [];

   /// <summary>Selected browser row.</summary>
   public StoreBrowserItem? SelectedItem { get => Get<StoreBrowserItem>(); set => Set(value, _ => UpdateState()); }

   /// <summary>An object row is selected and the list can be used.</summary>
   public bool CanActOnItem => CanBrowse && SelectedItem is { IsFolder: false };

   /// <summary>More objects exist after the loaded ones.</summary>
   public bool HasMore => _continuation is not null;

   private string? _continuation;

   #region Bucket editor

   /// <summary>The bucket editor is open.</summary>
   public bool IsBucketEditorOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>The editor creates a new bucket.</summary>
   public bool IsNewBucket { get => Get<bool>(); private set => Set(value); }

   private string? _bucketEditId;

   /// <summary>Name of the bucket being created.</summary>
   public string BucketDraftName { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Policy of the bucket being edited.</summary>
   public StorePolicyInfo? BucketDraftPolicy { get => Get<StorePolicyInfo>(); set => Set(value); }

   /// <summary>State of the bucket being edited.</summary>
   public StoreBucketState BucketDraftState { get => Get<StoreBucketState>(); set => Set(value); }

   /// <summary>Note of the bucket being edited.</summary>
   public string? BucketDraftNote { get => Get<string>(); set => Set(value); }

   /// <summary>Bucket states with captions.</summary>
   public StoreOption<StoreBucketState>[] BucketStates { get; } = [new(StoreBucketState.Active, "Active"), new(StoreBucketState.ReadOnly, "Read-only")];

   #endregion

   #region Versions sheet

   /// <summary>The version side sheet is open.</summary>
   public bool IsVersionsOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>Key whose versions are shown.</summary>
   public string VersionsKey { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Versions of <see cref="VersionsKey"/>, newest first.</summary>
   public ObservableCollection<StoreObjectVersionInfo> Versions { get; } = [];

   /// <summary>Selected version.</summary>
   public StoreObjectVersionInfo? SelectedVersion { get => Get<StoreObjectVersionInfo>(); set => Set(value, _ => UpdateState()); }

   #endregion

   private void RegisterBucketCommands() {
      RegisterCommand(nameof(NewBucketCommand), NewBucketCommand, () => CanBrowse && Policies.Count > 0);
      RegisterCommand(nameof(EditBucketCommand), EditBucketCommand, () => CanActOnBucket && !IsBrowsing);
      RegisterCommand(nameof(DeleteBucketCommand), DeleteBucketCommand, () => CanActOnBucket && !IsBrowsing);
      RegisterCommand(nameof(OpenBucketCommand), OpenBucketCommand, () => CanActOnBucket && !IsBrowsing);
      RegisterCommand(nameof(SaveBucketCommand), SaveBucketCommand, () => CanEdit && IsBucketEditorOpen);
      RegisterCommand(nameof(CloseBrowserCommand), CloseBrowserCommand, () => CanBrowse && IsBrowsing);
      RegisterCommand<StoreCrumb>(nameof(NavigateCrumbCommand), NavigateCrumbCommand, c => CanBrowse && IsBrowsing && c is not null && c.Prefix != Prefix);
      RegisterCommand(nameof(OpenItemCommand), OpenItemCommand, () => CanBrowse && IsBrowsing && SelectedItem is not null);
      RegisterCommand(nameof(UploadCommand), UploadCommand, () => CanBrowse && IsBrowsing && OpenBucket?.State == StoreBucketState.Active);
      RegisterCommand(nameof(DownloadCommand), DownloadCommand, () => CanActOnItem && IsBrowsing);
      RegisterCommand(nameof(DeleteObjectCommand), DeleteObjectCommand, () => CanActOnItem && IsBrowsing && OpenBucket?.State == StoreBucketState.Active);
      RegisterCommand(nameof(VersionsCommand), VersionsCommand, () => CanActOnItem && IsBrowsing);
      RegisterCommand(nameof(LoadMoreCommand), LoadMoreCommand, () => CanBrowse && IsBrowsing && HasMore);
      RegisterCommand(nameof(CloseEditorCommand), CloseEditorCommand, () => IsNotBusy);
      RegisterCommand(nameof(RestoreVersionCommand), RestoreVersionCommand,
         () => CanEdit && IsVersionsOpen && SelectedVersion is { IsDeleteMarker: false, IsCurrent: false } && OpenBucket?.State == StoreBucketState.Active);
      RegisterCommand(nameof(PurgeVersionCommand), PurgeVersionCommand, () => CanEdit && IsVersionsOpen && SelectedVersion is not null);
      RegisterCommand(nameof(DownloadVersionCommand), DownloadVersionCommand, () => CanEdit && IsVersionsOpen && SelectedVersion is { IsDeleteMarker: false });
   }

   private async Task LoadBucketsAsync() {
      var selected = SelectedBucket?.Id;
      Buckets.Clear();
      foreach (var bucket in await Service.GetMeta_StoreBuckets()) Buckets.Add(bucket);
      SelectedBucket = Buckets.FirstOrDefault(b => b.Id == selected);
   }

   /// <summary>Opens the editor for a new bucket.</summary>
   public void NewBucketCommand() {
      if (!CanBrowse) return;
      _bucketEditId = null;
      IsNewBucket = true;
      BucketDraftName = "";
      BucketDraftPolicy = Policies.FirstOrDefault(p => p.State == StorePolicyState.Active);
      BucketDraftState = StoreBucketState.Active;
      BucketDraftNote = null;
      IsBucketEditorOpen = true;
   }

   /// <summary>Opens the editor for the selected bucket.</summary>
   public void EditBucketCommand() {
      if (!CanActOnBucket || SelectedBucket is not { } bucket) return;
      _bucketEditId = bucket.Id;
      IsNewBucket = false;
      BucketDraftName = bucket.Name;
      BucketDraftPolicy = Policies.FirstOrDefault(p => p.Id == bucket.PolicyId);
      BucketDraftState = bucket.State;
      BucketDraftNote = bucket.Note;
      IsBucketEditorOpen = true;
   }

   /// <summary>Saves the bucket editor.</summary>
   public Task SaveBucketCommand() => RunAsync("Saving bucket...", async () => {
      var saved = await Service.PostGetMeta_StoreBucketSave(new StoreBucketSave {
         Id = _bucketEditId, Name = BucketDraftName.Trim(), PolicyId = BucketDraftPolicy?.Id ?? "", State = BucketDraftState, Note = BucketDraftNote
      });
      IsBucketEditorOpen = false;
      await LoadBucketsAsync();
      SelectedBucket = Buckets.FirstOrDefault(b => b.Id == saved.Id);
      Status = $"Bucket '{saved.Name}' saved.";
   });

   /// <summary>Deletes the selected empty bucket after confirmation.</summary>
   public async Task DeleteBucketCommand() {
      if (!CanActOnBucket || SelectedBucket is not { } bucket) return;
      if (Confirm?.Invoke($"Delete bucket '{bucket.Name}'? Only an empty bucket can be deleted.", "Delete bucket") != true) return;
      await RunAsync("Deleting bucket...", async () => {
         await Service.PostMeta_StoreBucketDelete(bucket.Id);
         await LoadBucketsAsync();
         Status = $"Bucket '{bucket.Name}' deleted.";
      });
   }

   /// <summary>Opens the object browser of the selected bucket.</summary>
   public async Task OpenBucketCommand() {
      if (!CanActOnBucket || SelectedBucket is not { } bucket) return;
      OpenBucket = bucket;
      Prefix = "";
      IsBrowsing = true;
      await RunAsync("Loading objects...", () => LoadObjectsAsync(false));
   }

   /// <summary>Returns from the object browser to the bucket table.</summary>
   public async Task CloseBrowserCommand() {
      IsBrowsing = false;
      Items.Clear();
      await RunAsync("Loading buckets...", LoadBucketsAsync);
   }

   /// <summary>Opens one step of the path.</summary>
   public Task NavigateCrumbCommand(StoreCrumb crumb) => GoToPrefixAsync(crumb.Prefix);

   /// <summary>Opens a folder row, or the version sheet of an object row.</summary>
   public Task OpenItemCommand() =>
      SelectedItem switch {
         { IsFolder: true, Prefix: { } prefix } => GoToPrefixAsync(prefix),
         { Object: not null } => VersionsCommand(),
         _ => Task.CompletedTask
      };

   private Task GoToPrefixAsync(string prefix) {
      Prefix = prefix;
      return RunAsync("Loading objects...", () => LoadObjectsAsync(false));
   }

   /// <summary>Loads the next page of objects.</summary>
   public Task LoadMoreCommand() => RunAsync("Loading objects...", () => LoadObjectsAsync(true));

   internal async Task LoadObjectsAsync(bool append) {
      if (OpenBucket is not { } bucket) return;
      if (!append) {
         Items.Clear();
         _continuation = null;
         Crumbs.Clear();
         Crumbs.Add(new StoreCrumb(bucket.Name, ""));
         var parts = Prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
         var path = "";
         foreach (var part in parts) {
            path += part + "/";
            Crumbs.Add(new StoreCrumb(part, path));
         }
      }
      var page = await Service.GetMeta_StoreObjects(bucket.Id, Prefix.Length == 0 ? null : Prefix, "/", _continuation, PageSize);
      foreach (var common in page.CommonPrefixes)
         Items.Add(new StoreBrowserItem(common[Prefix.Length..].TrimEnd('/'), common, null));
      foreach (var obj in page.Objects)
         Items.Add(new StoreBrowserItem(obj.Key[Prefix.Length..], null, obj));
      _continuation = page.NextContinuation;
      NotifyChanged(nameof(HasMore));
      Status = $"{Items.Count(i => i.IsFolder):N0} folders, {Items.Count(i => !i.IsFolder):N0} objects" + (HasMore ? " (more available)." : ".");
   }

   /// <summary>Lets the user choose files and uploads them to the current prefix.</summary>
   public Task UploadCommand() {
      var files = PickUploadFiles?.Invoke();
      return files is { Length: > 0 } ? UploadFilesAsync(files) : Task.CompletedTask;
   }

   /// <summary>Uploads local files to the current prefix of the open bucket, one after another; a file with the same key becomes a new version.</summary>
   public async Task UploadFilesAsync(IReadOnlyList<string> paths) {
      if (OpenBucket is not { } bucket || paths.Count == 0) return;
      var done = 0;
      await RunAsync("Uploading...", async () => {
         foreach (var path in paths.Where(File.Exists)) {
            WaiterText = $"Uploading {Path.GetFileName(path)} ({done + 1} of {paths.Count})...";
            await using var content = File.OpenRead(path);
            await Service.PostGetMeta_StoreObjectUpload(new StoreUploadRequest {
               BucketId = bucket.Id, Key = Prefix + Path.GetFileName(path), Mime = GuessMime(path)
            }, content);
            done++;
         }
         await LoadObjectsAsync(false);
         Status = $"{done:N0} file(s) uploaded.";
      });
      if (done < paths.Count && done > 0) Status += $" {paths.Count - done:N0} file(s) were not uploaded.";
   }

   /// <summary>Downloads the selected object's current version.</summary>
   public Task DownloadCommand() =>
      SelectedItem?.Object is { } obj ? DownloadAsync(obj.Key, null, obj.Name) : Task.CompletedTask;

   /// <summary>Downloads the selected version.</summary>
   public Task DownloadVersionCommand() =>
      SelectedVersion is { } version ? DownloadAsync(VersionsKey, version.Version, $"v{version.Version}-{VersionsKey[(VersionsKey.LastIndexOf('/') + 1)..]}") : Task.CompletedTask;

   private async Task DownloadAsync(string key, int? version, string fileName) {
      if (OpenBucket is not { } bucket) return;
      var target = PickSavePath?.Invoke(fileName);
      if (string.IsNullOrEmpty(target)) return;
      await RunAsync("Downloading...", async () => {
         try {
            await using (var source = await Service.GetMeta_StoreObjectDownload(bucket.Id, key, version))
            await using (var file = File.Create(target)) {
               await source.CopyToAsync(file);
            }
         }
         catch {
            try { File.Delete(target); } catch (IOException) { }
            throw;
         }
         Status = $"Downloaded to {target}.";
      });
   }

   /// <summary>Deletes the selected object (adds a delete marker) after confirmation.</summary>
   public async Task DeleteObjectCommand() {
      if (OpenBucket is not { } bucket || SelectedItem?.Object is not { } obj) return;
      if (Confirm?.Invoke($"Delete '{obj.Key}'? Its versions are kept and it can be restored from the version list.", "Delete object") != true) return;
      await RunAsync("Deleting object...", async () => {
         await Service.PostMeta_StoreObjectDelete(bucket.Id, obj.Key);
         await LoadObjectsAsync(false);
         Status = $"'{obj.Key}' deleted.";
      });
   }

   /// <summary>Opens the version sheet of the selected object.</summary>
   public async Task VersionsCommand() {
      if (OpenBucket is null || SelectedItem?.Object is not { } obj) return;
      VersionsKey = obj.Key;
      if (await RunAsync("Loading versions...", LoadVersionsAsync)) IsVersionsOpen = true;
   }

   private async Task LoadVersionsAsync() {
      Versions.Clear();
      foreach (var version in await Service.GetMeta_StoreObjectVersions(OpenBucket!.Id, VersionsKey)) Versions.Add(version);
      SelectedVersion = null;
   }

   /// <summary>Makes the selected version current again.</summary>
   public Task RestoreVersionCommand() => RunAsync("Restoring version...", async () => {
      if (SelectedVersion is not { } version) return;
      var restored = await Service.PostGetMeta_StoreObjectRestore(OpenBucket!.Id, VersionsKey, version.Version);
      await LoadVersionsAsync();
      await LoadObjectsAsync(false);
      Status = $"Version {version.Version} restored as version {restored.Version}.";
   });

   /// <summary>Permanently deletes the selected version after confirmation.</summary>
   public async Task PurgeVersionCommand() {
      if (SelectedVersion is not { } version) return;
      if (Confirm?.Invoke($"Permanently delete version {version.Version} of '{VersionsKey}'? This cannot be undone.", "Delete version") != true) return;
      await RunAsync("Deleting version...", async () => {
         await Service.PostMeta_StoreObjectVersionPurge(OpenBucket!.Id, VersionsKey, version.Version);
         Versions.Clear();
         try { await LoadVersionsAsync(); }
         catch (Em.Shared.ActionException) { IsVersionsOpen = false; }
         await LoadObjectsAsync(false);
         Status = $"Version {version.Version} permanently deleted.";
      });
   }

   /// <summary>Closes the open editor or side sheet without saving.</summary>
   public void CloseEditorCommand() {
      if (IsBusy) return;
      IsBucketEditorOpen = IsVolumeEditorOpen = IsPolicyEditorOpen = IsVersionsOpen = false;
   }

   /// <summary>MIME type from a file extension, for uploads; unknown extensions use <c>application/octet-stream</c>.</summary>
   public static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
      ".txt" or ".log" => "text/plain",
      ".csv" => "text/csv",
      ".json" => "application/json",
      ".xml" => "application/xml",
      ".pdf" => "application/pdf",
      ".png" => "image/png",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".gif" => "image/gif",
      ".webp" => "image/webp",
      ".svg" => "image/svg+xml",
      ".zip" => "application/zip",
      ".gz" => "application/gzip",
      ".mp4" => "video/mp4",
      ".mp3" => "audio/mpeg",
      ".html" or ".htm" => "text/html",
      ".css" => "text/css",
      ".js" => "text/javascript",
      ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      _ => "application/octet-stream"
   };
}
