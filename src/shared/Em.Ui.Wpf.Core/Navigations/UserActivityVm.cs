using System.Collections.ObjectModel;
using Em.Api.Core.Models;
using Em.Shared;
using Em.Ui.Wpf.Core;
using Em.Ui.Wpf.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Ui.Wpf.Navigations;

/// <summary>One entry of the account timeline.</summary>
public sealed class UserActivityItem(AuditActivity entry)
{
   /// <summary>The audit entry.</summary>
   public AuditActivity Entry { get; } = entry;

   /// <summary>Main line.</summary>
   public string Title => Entry.Summary ?? Entry.KindLabel ?? Entry.Kind;

   /// <summary>When, by whom and from where.</summary>
   public string Subtitle {
      get {
         var when = DateTime.SpecifyKind(Entry.Time, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy, HH\\:mm");
         var by = Entry.ActorType == AuditActorTypes.Anonymous ? null : $"by {LogManagerVm.ActorText(Entry)}";
         return string.Join(" - ", new[] { when, by, Entry.ClientAddress }.Where(s => !string.IsNullOrEmpty(s)));
      }
   }

   /// <summary>A failure or a refusal (red dot).</summary>
   public bool IsDanger => Entry.Kind.Contains("failed", StringComparison.Ordinal) || Entry.Kind is "token.rejected" or "deleted";

   /// <summary>A security-relevant change (amber dot).</summary>
   public bool IsWarning => Entry.Group == "System.Password" || Entry.Kind is "session.revoked" or "admin.toggled" or "debug.impersonate";

   /// <summary>A creation (green dot).</summary>
   public bool IsSuccess => Entry.Kind == "created";
}

/// <summary>
/// The Activity tab of the User Editor: the account's audit history (<c>System.User</c>, <c>System.Auth</c>,
/// <c>System.Password</c>) newest first, loaded when the tab is first shown, with Load more.
/// </summary>
public sealed class UserActivityVm : MvvmModelBase
{
   private const int PageSize = 20;
   private const string Groups = "System.User,System.Auth,System.Password";
   private readonly IAuditServices? _service;
   private string? _userId;
   private string? _cursor;
   private bool _loaded;

   /// <summary>Creates the view model; the service comes from <see cref="MvvmModelBase.EmApp"/>.</summary>
   public UserActivityVm() {
      RegisterCommand(nameof(LoadMoreCommand), LoadMoreCommand, () => IsNotBusy && _cursor is not null);
      RegisterCommand(nameof(ReloadCommand), ReloadCommand, () => IsNotBusy && _userId is not null);
   }

   /// <summary>Creates the view model with an explicit service, for tests.</summary>
   public UserActivityVm(IAuditServices service) : this() => _service = service;

   private IAuditServices Service => _service ?? EmApp!.ServiceProvider.GetRequiredService<IAuditServices>();

   /// <summary>Entries, newest first.</summary>
   public ObservableCollection<UserActivityItem> Items { get; } = [];

   /// <summary>More entries can be loaded.</summary>
   public bool HasMore => _cursor is not null;

   /// <summary>Nothing to show.</summary>
   public bool IsEmpty => _loaded && Items.Count == 0 && Message is null;

   /// <summary>Why the history could not be loaded, or a partial-data warning.</summary>
   public string? Message { get => Get<string>(); private set => Set(value, _ => NotifyChanged(nameof(HasMessage))); }

   /// <summary>A message is shown.</summary>
   public bool HasMessage => !string.IsNullOrEmpty(Message);

   /// <summary>The tab is selected; the first selection loads the history.</summary>
   public bool IsActive {
      get => Get<bool>();
      set => Set(value, active => { if (active && !_loaded && _userId is not null) _ = LoadAsync(false); });
   }

   /// <summary>Points the tab at another account (or none); the history is loaded again on the next selection.</summary>
   public void Reset(string? userId) {
      _userId = string.IsNullOrEmpty(userId) ? null : userId;
      _cursor = null;
      _loaded = false;
      Items.Clear();
      Message = null;
      Notify();
      if (IsActive && _userId is not null) _ = LoadAsync(false);
   }

   /// <summary>Loads the next page.</summary>
   public Task LoadMoreCommand() => LoadAsync(true);

   /// <summary>Reloads from the newest entry.</summary>
   public Task ReloadCommand() => LoadAsync(false);

   private async Task LoadAsync(bool more) {
      if (_userId is not { } userId || IsBusy || (_service is null && EmApp is null)) return;
      try {
         IsBusy = true;
         Notify();
         var page = await Service.GetMeta_AuditActivity_ByTarget("user", userId, Groups, more ? _cursor : null, PageSize);
         if (userId != _userId) return;
         if (!more) Items.Clear();
         foreach (var item in page.Items) Items.Add(new UserActivityItem(item));
         _cursor = page.NextCursor;
         Message = page.Warnings.Length > 0 ? $"Some history is not available: {string.Join("; ", page.Warnings)}" : null;
         _loaded = true;
      }
      catch (ActionException ex) {
         Message = ex.StatusCode switch {
            403 => "You may not read the history of this account.",
            404 => "Account history is not available on this server (the log engine is off).",
            _ => ex.Message
         };
         _loaded = true;
      }
      catch (Exception ex) {
         Message = $"The history could not be loaded: {ex.Message}";
      }
      finally {
         IsBusy = false;
         Notify();
      }
   }

   private void Notify() {
      NotifyChanged(nameof(HasMore));
      NotifyChanged(nameof(IsEmpty));
      foreach (var command in Commands) command.RaiseCanExecuteChanged();
   }
}
