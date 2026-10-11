using System.Collections.ObjectModel;
using Em.Api.Core.Models;
using Em.Ui.Wpf.Shared;
using Em.Shared;
using Em.Ui.Wpf.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Ui.Wpf.Navigations;

/// <summary>One panel of the Object Storage Manager tab strip.</summary>
/// <param name="Key">Stable key of the panel.</param>
/// <param name="Title">Tab caption.</param>
public sealed record StoreTab(string Key, string Title);

/// <summary>An enum value with its UI caption, for combo boxes.</summary>
/// <typeparam name="T">Enum type.</typeparam>
/// <param name="Value">Value.</param>
/// <param name="Caption">Caption shown in the list.</param>
public sealed record StoreOption<T>(T Value, string Caption);

/// <summary>
/// State and commands of the Object Storage Manager: buckets with their object browser and version sheet,
/// volumes, policies and maintenance. The view supplies dialogs through <see cref="Confirm"/>,
/// <see cref="PickUploadFiles"/> and <see cref="PickSavePath"/>.
/// </summary>
public sealed partial class ObjectStorageManagerVm : MvvmModelBase
{
   internal const string BucketsTab = "buckets";
   internal const string VolumesTab = "volumes";
   internal const string PoliciesTab = "policies";
   internal const string MaintenanceTab = "maintenance";

   private readonly IObjectStorageServices? _service;

   /// <summary>Creates a view model for navigation; the service comes from the application.</summary>
   public ObjectStorageManagerVm() { RegisterCommands(); }

   /// <summary>Creates a view model with an explicit service, for tests and other hosts.</summary>
   public ObjectStorageManagerVm(IObjectStorageServices service) {
      _service = service;
      RegisterCommands();
   }

   private IObjectStorageServices Service => _service ?? EmApp!.ServiceProvider.GetRequiredService<IObjectStorageServices>();

   /// <summary>Asks the user a yes/no question (message, title); <c>null</c> answers no.</summary>
   public Func<string, string, bool>? Confirm { get; set; }

   /// <summary>Lets the user choose files to upload; <c>null</c> or empty cancels.</summary>
   public Func<string[]?>? PickUploadFiles { get; set; }

   /// <summary>Lets the user choose where to save a download, given a suggested file name; <c>null</c> cancels.</summary>
   public Func<string, string?>? PickSavePath { get; set; }

   /// <summary>Panels of the screen.</summary>
   public ObservableCollection<StoreTab> Tabs { get; } = [
      new(BucketsTab, "Buckets"), new(VolumesTab, "Volumes"), new(PoliciesTab, "Policies"), new(MaintenanceTab, "Maintenance")
   ];

   /// <summary>Selected panel.</summary>
   public StoreTab ActiveTab {
      get => Get<StoreTab>() ?? Tabs[0];
      set => Set(value, _ => {
         NotifyChanged(nameof(IsBucketsTab));
         NotifyChanged(nameof(IsVolumesTab));
         NotifyChanged(nameof(IsPoliciesTab));
         NotifyChanged(nameof(IsMaintenanceTab));
      });
   }

   /// <summary>The Buckets panel is shown.</summary>
   public bool IsBucketsTab => ActiveTab.Key == BucketsTab;

   /// <summary>The Volumes panel is shown.</summary>
   public bool IsVolumesTab => ActiveTab.Key == VolumesTab;

   /// <summary>The Policies panel is shown.</summary>
   public bool IsPoliciesTab => ActiveTab.Key == PoliciesTab;

   /// <summary>The Maintenance panel is shown.</summary>
   public bool IsMaintenanceTab => ActiveTab.Key == MaintenanceTab;

   /// <summary>True after the first successful load.</summary>
   public bool IsLoaded { get => Get<bool>(); private set => Set(value); }

   /// <summary>Result of the last operation.</summary>
   public string Status { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>An editor or side sheet is open.</summary>
   public bool IsEditorOpen => IsBucketEditorOpen || IsVolumeEditorOpen || IsPolicyEditorOpen || IsVersionsOpen;

   /// <summary>Lists and toolbars can be used.</summary>
   public bool CanBrowse => IsLoaded && IsNotBusy && !IsEditorOpen;

   /// <summary>Editor inputs can be changed.</summary>
   public bool CanEdit => IsLoaded && IsNotBusy;

   private void RegisterCommands() {
      RegisterCommand(nameof(RefreshCommand), RefreshCommand, () => IsNotBusy && !IsEditorOpen);
      RegisterBucketCommands();
      RegisterVolumeCommands();
      RegisterPolicyCommands();
      RegisterMaintenanceCommands();
   }

   /// <summary>Loads buckets, volumes, policies and status.</summary>
   public Task ReloadAsync() => RunAsync(IsBucketPage ? "Loading bucket..." : "Loading object storage...", async () => {
      if (IsBucketPage) {
         await LoadBucketPageCoreAsync();
         return;
      }
      await LoadSettingsAsync();
      await LoadBucketsAsync();
      await LoadStatusAsync();
      if (IsBrowsing && OpenBucket is { } bucket) {
         OpenBucket = Buckets.FirstOrDefault(b => b.Id == bucket.Id);
         if (OpenBucket is null) IsBrowsing = false; else await LoadObjectsAsync(false);
      }
      IsLoaded = true;
      Status = "Object storage loaded.";
   });

   /// <summary>Reloads everything shown.</summary>
   public Task RefreshCommand() => ReloadAsync();

   private async Task LoadSettingsAsync() {
      Replace(Volumes, await Service.GetMeta_StoreVolumes(), v => v.Id, ref _selectedVolumeId);
      Replace(Policies, await Service.GetMeta_StorePolicies(), p => p.Id, ref _selectedPolicyId);
      SelectedVolume = Volumes.FirstOrDefault(v => v.Id == _selectedVolumeId);
      SelectedPolicy = Policies.FirstOrDefault(p => p.Id == _selectedPolicyId);
   }

   private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows, Func<T, string> id, ref string? selectedId) {
      target.Clear();
      foreach (var row in rows) target.Add(row);
      var keep = selectedId;
      if (keep is not null && target.All(r => id(r) != keep)) selectedId = null;
   }

   /// <summary>Runs an operation with the wait overlay; server rejections are shown in <see cref="Status"/>.</summary>
   internal async Task<bool> RunAsync(string caption, Func<Task> action) {
      if (IsBusy || (_service is null && EmApp is null)) return false;
      try {
         IsBusy = InWaiting = true;
         WaiterText = caption;
         UpdateState();
         await action();
         return true;
      }
      catch (ActionException ex) {
         Status = ex.Message.Replace("Server Error: ", "");
      }
      catch (Exception ex) {
         Status = "The operation did not complete. Try reloading.";
         if (EmApp is not null) AlertError(ex);
      }
      finally {
         InWaiting = IsBusy = false;
         UpdateState();
      }
      return false;
   }

   internal void UpdateState() {
      NotifyChanged(nameof(IsEditorOpen));
      NotifyChanged(nameof(CanBrowse));
      NotifyChanged(nameof(CanEdit));
      NotifyChanged(nameof(CanActOnBucket));
      NotifyChanged(nameof(CanActOnItem));
      foreach (var command in Commands) command.RaiseCanExecuteChanged();
   }

   /// <summary>Formats a byte count for display.</summary>
   public static string FormatSize(long bytes) => CdnManagerVm.FormatSize(bytes);

   /// <summary>Formats an optional limit; <c>null</c> reads "Unlimited".</summary>
   public static string FormatLimit(long? bytes) => bytes is { } b ? FormatSize(b) : "Unlimited";
}
