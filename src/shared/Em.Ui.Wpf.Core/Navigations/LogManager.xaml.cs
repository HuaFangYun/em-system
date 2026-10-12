using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Em.Api.Core.Models;
using Em.Ui.Core.Shared;
using Em.Ui.Wpf.Core;
using Em.Ui.Wpf.Shared;
using UserControl = System.Windows.Controls.UserControl;

namespace Em.Ui.Wpf.Navigations;

/// <summary>
/// Log Manager: summary, audit, security, grouped errors, the server log, and the log store and retention settings.
/// Tabs follow the caller's <c>core.log</c> claims. Guide: <c>doc/engine/engine-log.md</c>.
/// </summary>
public partial class LogManager : UserControl, INavigationBody
{
   /// <summary>Creates the Log Manager.</summary>
   public LogManager(EmApp app) {
      InitializeComponent();
      Vm.EmApp = app;
      Window Owner() => Window.GetWindow(this) ?? app.MainWindow;
      Vm.Confirm = (message, title) => Owner().ShowMboxDecideWarning(message, title) == MessageBoxResult.Yes;
      Vm.PasswordReset = () => {
         storePassword.Clear();
         ownerPassword.Clear();
      };
   }

   /// <summary>Screen view model.</summary>
   public LogManagerVm Vm => (LogManagerVm)DataContext;

   private void OnStorePasswordChanged(object sender, RoutedEventArgs args) => Vm.StoreDraftPassword = storePassword.Password;

   private void OnOwnerPasswordChanged(object sender, RoutedEventArgs args) => Vm.PrepareOwnerPassword = ownerPassword.Password;

   private void OnRowDoubleClick(object sender, MouseButtonEventArgs args) {
      if (args.ChangedButton != MouseButton.Left || !Vm.CanBrowse || sender is not ListBoxItem item) return;
      args.Handled = true;
      switch (item.DataContext) {
         case LogAuditRow audit:
            Vm.SelectedAudit = audit;
            Vm.OpenAuditCommand();
            break;
         case LogEventRow e:
            Vm.SelectedEvent = e;
            Vm.OpenEventCommand();
            break;
         case LogErrorRow error:
            Vm.SelectedError = error;
            _ = Vm.OpenErrorCommand();
            break;
         case LogStoreRow store when Vm.CanEditStores:
            Vm.SelectedStore = store;
            Vm.EditStoreCommand();
            break;
      }
   }

   private void OnPreviewKeyDown(object sender, KeyEventArgs args) {
      if (args.Key == Key.Escape && Vm.IsSheetOpen && Vm.IsNotBusy) {
         Vm.CloseSheetCommand();
         args.Handled = true;
      }
   }

   /// <inheritdoc />
   public Task OnNavigatingIn(INavigation sender, NavigatingEventArgs args) => Vm.IsLoaded ? Task.CompletedTask : Vm.ReloadAsync();

   /// <inheritdoc />
   public Task OnNavigatingAway(INavigation sender, NavigatingEventArgs args) => Task.CompletedTask;

   /// <inheritdoc />
   public Task OnReloadRequested(INavigation sender, NavigationEventArgs args) => Vm.ReloadAsync();

   /// <inheritdoc />
   public Task OnRelease(INavigation sender) {
      Vm.CloseSheetCommand();
      return Task.CompletedTask;
   }

   /// <summary>Error state parameter of the Acknowledge button.</summary>
   public static readonly LogErrorState Acknowledged = LogErrorState.Acknowledged;
   /// <summary>Error state parameter of the Resolved button.</summary>
   public static readonly LogErrorState Resolved = LogErrorState.Resolved;
   /// <summary>Error state parameter of the Ignore button.</summary>
   public static readonly LogErrorState Ignored = LogErrorState.Ignored;
   /// <summary>Error state parameter of the Reopen button.</summary>
   public static readonly LogErrorState New = LogErrorState.New;

   /// <summary>UTC time to a local time caption.</summary>
   public static IValueConverter LocalTimeConverter { get; } = new FuncConverter(v => v is DateTime t ? LogManagerVm.Local(t) : "");

   private sealed class FuncConverter(Func<object?, object> convert) : IValueConverter
   {
      public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
      public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
   }
}
