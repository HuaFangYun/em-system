using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Em.Ui.Core.Shared;
using Em.Ui.Wpf.Core;
using Em.Ui.Wpf.Shared;
using UserControl = System.Windows.Controls.UserControl;

namespace Em.Ui.Wpf.Navigations;

/// <summary>
/// Bucket editor: the settings of one bucket and its objects with their versions on one screen, opened with a
/// <see cref="StoreBucketNavigationPayload"/> through <see cref="EmApp.ObjectStorageBucketNavigationName"/>, so
/// any screen can hand a bucket over. Uses the same view model as <see cref="ObjectStorageManager"/>, in
/// bucket page mode.
/// </summary>
public partial class StoreBucketEditor : UserControl, INavigationBody
{
   private StoreBucketNavigationPayload? _payload;

   /// <summary>Creates the bucket editor.</summary>
   public StoreBucketEditor(EmApp app) {
      InitializeComponent();
      ObjectStorageManager.AttachDialogs(Vm, this, app);
   }

   /// <summary>Screen view model.</summary>
   public ObjectStorageManagerVm Vm => (ObjectStorageManagerVm)DataContext;

   private void OnRowDoubleClick(object sender, MouseButtonEventArgs args) {
      if (args.ChangedButton != MouseButton.Left || !Vm.CanBrowse || sender is not ListBoxItem { DataContext: StoreBrowserItem entry }) return;
      args.Handled = true;
      Vm.SelectedItem = entry;
      _ = Vm.OpenItemCommand();
   }

   private void OnObjectsDragOver(object sender, DragEventArgs args) {
      args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) && Vm.IsBrowsing && Vm.CanBrowse && Vm.CanChangeContents ? DragDropEffects.Copy : DragDropEffects.None;
      args.Handled = true;
   }

   private async void OnObjectsDrop(object sender, DragEventArgs args) {
      if (!Vm.IsBrowsing || !Vm.CanBrowse || !Vm.CanChangeContents || args.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
      args.Handled = true;
      // Folders are not uploaded recursively; only dropped files are.
      await Vm.UploadFilesAsync(files.Where(System.IO.File.Exists).ToArray());
   }

   private void OnPreviewKeyDown(object sender, KeyEventArgs args) {
      if (args.Key == Key.Escape && Vm.IsEditorOpen && Vm.IsNotBusy) {
         Vm.CloseEditorCommand();
         args.Handled = true;
      }
   }

   // Only the payload is taken here: back and forward also raise OnNavigatingIn and must not reload the page.
   /// <inheritdoc />
   public Task OnNavigatingIn(INavigation sender, NavigatingEventArgs args) {
      if (args.Data is not StoreBucketNavigationPayload payload) {
         args.Cancel = true;
         args.Message = "The bucket editor can only be opened with a bucket payload.";
         return Task.CompletedTask;
      }
      _payload = payload;
      return Task.CompletedTask;
   }

   /// <inheritdoc />
   public Task OnNavigatingAway(INavigation sender, NavigatingEventArgs args) => Task.CompletedTask;

   /// <inheritdoc />
   public Task OnReloadRequested(INavigation sender, NavigationEventArgs args) {
      if (args.Data is StoreBucketNavigationPayload payload && !ReferenceEquals(payload, _payload)) _payload = payload;
      if (!Vm.IsBucketPage && _payload is { } first) return Vm.LoadBucketPageAsync(first);
      return Vm.ReloadAsync();
   }

   /// <inheritdoc />
   public Task OnRelease(INavigation sender) {
      Vm.CloseEditorCommand();
      return Task.CompletedTask;
   }
}
