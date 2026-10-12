using System.Collections.ObjectModel;
using Em.Api.Core.Models;
using Em.Shared;

namespace Em.Ui.Wpf.Navigations;

/// <summary>One audit group row of the retention settings; <see cref="Months"/> empty means never delete.</summary>
public sealed class LogAuditRetentionRow(LogAuditRetention rule) : NotifyPropertyBase
{
   /// <summary>Group.</summary>
   public string Group { get; } = rule.Group;
   /// <summary>Engine group.</summary>
   public bool IsSystem { get; } = rule.IsSystem;
   /// <summary>Lowest allowed months.</summary>
   public int? MinMonths { get; } = rule.MinMonths;

   /// <summary>Months to keep as typed; empty = never delete (module groups only).</summary>
   public string Months { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Help text of the row.</summary>
   public string Help => IsSystem
      ? $"Engine audit; at least {MinMonths ?? 12} months, cannot be kept forever."
      : MinMonths is { } min ? $"At least {min} months (set by the module); empty keeps it forever." : "Empty keeps it forever.";

   internal LogAuditRetention ToRule() => new() {
      Group = Group, IsSystem = IsSystem, MinMonths = MinMonths,
      Months = int.TryParse(Months.Trim(), out var m) ? m : null
   };

   internal static LogAuditRetentionRow From(LogAuditRetention r) => new(r) { Months = r.Months?.ToString() ?? "" };
}

/// <summary>A store row with its display texts.</summary>
public sealed class LogStoreRow(LogStoreDraft draft, LogStoreInfo? info)
{
   /// <summary>The draft as it will be saved.</summary>
   public LogStoreDraft Draft { get; } = draft;
   /// <summary>Last known state from the server.</summary>
   public LogStoreInfo? Info { get; } = info;
   /// <summary>Name.</summary>
   public string Name => Draft.Name;
   /// <summary>Role caption.</summary>
   public string Role => Draft.Role.ToString();
   /// <summary>Where the store is.</summary>
   public string Location => Draft.Kind == LogStoreKind.Internal ? "Internal database" : $"{Draft.Provider}: {Draft.Server}/{Draft.Database}";
   /// <summary>Data range.</summary>
   public string Range => Info?.OldestEventUtc is { } o && Info.NewestEventUtc is { } n ? $"{LogManagerVm.Local(o)} - {LogManagerVm.Local(n)}" : "";
}

public sealed partial class LogManagerVm
{
   /// <summary>Settings sub-panels.</summary>
   public LogOption<string>[] SettingsPanels { get; } = [new("database", "Database"), new("retention", "Retention")];

   /// <summary>Selected settings sub-panel.</summary>
   public string SettingsPanel {
      get => Get<string>() ?? "database";
      set => Set(value, _ => { NotifyChanged(nameof(IsDatabasePanel)); NotifyChanged(nameof(IsRetentionPanel)); });
   }

   /// <summary>Database panel shown.</summary>
   public bool IsDatabasePanel => SettingsPanel == "database";

   /// <summary>Retention panel shown.</summary>
   public bool IsRetentionPanel => SettingsPanel == "retention";

   private void RegisterSettingsCommands() {
      RegisterCommand(nameof(AddStoreCommand), AddStoreCommand, () => CanEditStores);
      RegisterCommand(nameof(EditStoreCommand), EditStoreCommand, () => CanEditStores && SelectedStore is not null);
      RegisterCommand(nameof(RemoveStoreCommand), RemoveStoreCommand, () => CanEditStores && SelectedStore is not null);
      RegisterCommand(nameof(ApplyStoreCommand), ApplyStoreCommand, () => IsNotBusy && IsStoreEditorOpen);
      RegisterCommand(nameof(TestStoreCommand), TestStoreCommand, () => IsNotBusy && IsStoreEditorOpen);
      RegisterCommand(nameof(OpenPrepareCommand), OpenPrepareCommand, () => IsNotBusy && IsStoreEditorOpen && StoreDraftIsConnection);
      RegisterCommand(nameof(RunPrepareCommand), RunPrepareCommand, () => IsNotBusy && IsPrepareOpen);
      RegisterCommand(nameof(ClosePrepareCommand), ClosePrepareCommand, () => IsNotBusy && IsPrepareOpen);
      RegisterCommand(nameof(SaveStoresCommand), SaveStoresCommand, () => CanEditStores && StoresDirty);
      RegisterCommand(nameof(PreviewRetentionCommand), PreviewRetentionCommand, () => CanBrowse && Retention is not null);
      RegisterCommand(nameof(SaveRetentionCommand), SaveRetentionCommand, () => CanBrowse && Retention is not null);
   }

   private Task LoadSettingsAsync() => Task.WhenAll(LoadStoresAsync(), LoadRetentionAsync());

   private void CloseSettingsSheets() {
      IsStoreEditorOpen = false;
      IsPrepareOpen = false;
   }

   #region Database

   private LogStoreSettingsView? _storeView;

   /// <summary>Stores as they will be saved.</summary>
   public ObservableCollection<LogStoreRow> Stores { get; } = [];

   /// <summary>Stores the running server uses.</summary>
   public ObservableCollection<LogStoreRow> ActiveStores { get; } = [];

   /// <summary>Selected store.</summary>
   public LogStoreRow? SelectedStore { get => Get<LogStoreRow>(); set => Set(value, _ => UpdateState()); }

   /// <summary>Notice about restart or the environment override.</summary>
   public string StoreNotice { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>The list was changed and not saved.</summary>
   public bool StoresDirty { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>The list may be edited (no environment override).</summary>
   public bool CanEditStores => CanBrowse && _storeView is { EnvironmentOverride: false };

   /// <summary>Providers offered for a connection store.</summary>
   public LogOption<LogStoreProvider>[] Providers { get; } = [
      new(LogStoreProvider.MicrosoftSqlServer, "SQL Server"), new(LogStoreProvider.MySql, "MySQL"), new(LogStoreProvider.PostgreSql, "PostgreSQL")
   ];

   /// <summary>Roles.</summary>
   public LogOption<LogStoreRole>[] Roles { get; } = [
      new(LogStoreRole.Write, "Write (new events go here)"), new(LogStoreRole.Read, "Read (older data, searched)"), new(LogStoreRole.Detached, "Detached (kept in the list only)")
   ];

   /// <summary>Kinds.</summary>
   public LogOption<LogStoreKind>[] Kinds { get; } = [new(LogStoreKind.Internal, "Internal database"), new(LogStoreKind.Connection, "Another database")];

   /// <summary>The store editor is open.</summary>
   public bool IsStoreEditorOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>Draft being edited (a copy; applied to the list with Apply).</summary>
   public LogStoreDraft StoreDraft { get => Get<LogStoreDraft>() ?? new LogStoreDraft(); private set => Set(value); }

   /// <summary>Draft kind; switching shows or hides the connection fields.</summary>
   public LogStoreKind StoreDraftKind {
      get => StoreDraft.Kind;
      set { StoreDraft.Kind = value; NotifyChanged(nameof(StoreDraftKind)); NotifyChanged(nameof(StoreDraftIsConnection)); UpdateState(); }
   }

   /// <summary>The draft is a connection store.</summary>
   public bool StoreDraftIsConnection => StoreDraft.Kind == LogStoreKind.Connection;

   /// <summary>Port as typed; empty = provider default.</summary>
   public string StoreDraftPort { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>New runtime password typed in the editor; empty keeps the stored one. Set by the view.</summary>
   public string? StoreDraftPassword { get; set; }

   /// <summary>Result of Test.</summary>
   public string StoreTestText { get => Get<string>() ?? ""; private set => Set(value); }

   private LogStoreRow? _editing;

   private async Task LoadStoresAsync() {
      _storeView = await Service.GetMeta_LogStores();
      Stores.Clear();
      foreach (var s in _storeView.Stores) Stores.Add(new LogStoreRow(ToDraft(s), s));
      ActiveStores.Clear();
      foreach (var s in _storeView.Active) ActiveStores.Add(new LogStoreRow(ToDraft(s), s));
      StoresDirty = false;
      StoreNotice = _storeView.EnvironmentOverride
         ? "EM_LOG_DB_CONNECTION overrides the write store on this server; the list is shown but cannot be saved."
         : _storeView.RestartRequired ? "Saved (restart required): the API uses the active stores below until it restarts." : "Active now.";
      UpdateState();
   }

   private static LogStoreDraft ToDraft(LogStoreInfo s) => new() {
      Id = s.Id, Name = s.Name, Kind = s.Kind, Role = s.Role, Provider = s.Provider, Server = s.Server, Port = s.Port, Database = s.Database,
      IntegratedSecurity = s.IntegratedSecurity, User = s.User, Encrypt = s.Encrypt, TrustServerCertificate = s.TrustServerCertificate
   };

   /// <summary>Opens the editor for a new connection store.</summary>
   public void AddStoreCommand() {
      _editing = null;
      OpenStoreEditor(new LogStoreDraft { Name = "Log database", Kind = LogStoreKind.Connection, Role = LogStoreRole.Read, Encrypt = true });
   }

   /// <summary>Opens the editor for the selected store.</summary>
   public void EditStoreCommand() {
      if (SelectedStore is not { } row) return;
      _editing = row;
      var d = row.Draft;
      OpenStoreEditor(new LogStoreDraft {
         Id = d.Id, Name = d.Name, Kind = d.Kind, Role = d.Role, Provider = d.Provider, Server = d.Server, Port = d.Port, Database = d.Database,
         IntegratedSecurity = d.IntegratedSecurity, User = d.User, Password = d.Password, Encrypt = d.Encrypt, TrustServerCertificate = d.TrustServerCertificate
      });
   }

   private void OpenStoreEditor(LogStoreDraft draft) {
      StoreDraft = draft;
      StoreDraftPort = draft.Port?.ToString() ?? "";
      StoreDraftPassword = null;
      StoreTestText = "";
      NotifyChanged(nameof(StoreDraftKind));
      NotifyChanged(nameof(StoreDraftIsConnection));
      PasswordReset?.Invoke();
      IsStoreEditorOpen = true;
   }

   /// <summary>Asks the view to clear its password boxes.</summary>
   public Action? PasswordReset { get; set; }

   /// <summary>Removes the selected store from the list (its database is not touched).</summary>
   public void RemoveStoreCommand() {
      if (SelectedStore is not { } row) return;
      if (row.Draft.Role == LogStoreRole.Write) {
         Status = "Choose another write store first.";
         return;
      }
      Stores.Remove(row);
      StoresDirty = true;
   }

   private LogStoreDraft CollectDraft() {
      var d = StoreDraft;
      if (string.IsNullOrWhiteSpace(StoreDraftPort)) d.Port = null;
      else if (int.TryParse(StoreDraftPort.Trim(), out var port) && port is > 0 and < 65536) d.Port = port;
      else throw new ActionException("The port must be 1-65535, or empty for the default.", 400);
      if (!string.IsNullOrEmpty(StoreDraftPassword)) d.Password = StoreDraftPassword;
      return d;
   }

   /// <summary>Puts the draft into the list; the list is saved with Save.</summary>
   public void ApplyStoreCommand() {
      LogStoreDraft draft;
      try { draft = CollectDraft(); }
      catch (ActionException ex) { Status = ex.Message; return; }
      if (string.IsNullOrWhiteSpace(draft.Name)) { Status = "The store needs a name."; return; }
      if (draft.Role == LogStoreRole.Write) {
         // Only one write store: the previous one becomes a read store.
         for (var i = 0; i < Stores.Count; i++) {
            var other = Stores[i];
            if (other != _editing && other.Draft.Role == LogStoreRole.Write) {
               other.Draft.Role = LogStoreRole.Read;
               Stores[i] = new LogStoreRow(other.Draft, other.Info);
            }
         }
      }
      var row = new LogStoreRow(draft, _editing?.Info);
      if (_editing is not null && Stores.IndexOf(_editing) is var index and >= 0) Stores[index] = row;
      else Stores.Add(row);
      SelectedStore = row;
      StoresDirty = true;
      IsStoreEditorOpen = false;
      Status = "Store changed; Save the list to keep it (it applies after the API restarts).";
   }

   /// <summary>Tests the draft with its runtime credential.</summary>
   public Task TestStoreCommand() => RunAsync("Testing the store...", async () => {
      var result = await Service.PostGetMeta_LogStoreTest(CollectDraft());
      StoreTestText = result.Message + (result.SchemaVersion is { } v ? $" Schema version {v} (engine {result.EngineSchemaVersion})." : "");
   });

   /// <summary>Saves the list.</summary>
   public Task SaveStoresCommand() => RunAsync("Saving stores...", async () => {
      if (_storeView is null) return;
      if (Confirm is { } confirm && !confirm("Save the log store list? It applies after the API restarts. Data is not moved between stores.", "Save log stores"))
         return;
      _storeView = await Service.PostGetMeta_LogStoreSave(new LogStoreSave { Revision = _storeView.Revision, Stores = [.. Stores.Select(s => s.Draft)] });
      await LoadStoresAsync();
      Status = "Log stores saved. Restart the API to use them.";
   });

   #endregion

   #region Prepare

   /// <summary>The Prepare sheet is open.</summary>
   public bool IsPrepareOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>Owner user for Prepare (not stored); empty uses integrated authentication.</summary>
   public string? PrepareOwnerUser { get => Get<string>(); set => Set(value); }

   /// <summary>Owner password for Prepare (not stored). Set by the view.</summary>
   public string? PrepareOwnerPassword { get; set; }

   /// <summary>Create the database when it does not exist.</summary>
   public bool PrepareCreateDatabase { get => Get<bool>(); set => Set(value); }

   /// <summary>Grant the runtime user its rights.</summary>
   public bool PrepareGrantRuntime { get => Get<bool?>() ?? true; set => Set(value); }

   /// <summary>Steps of the last Prepare.</summary>
   public ObservableCollection<LogPrepareStep> PrepareSteps { get; } = [];

   /// <summary>Opens the Prepare sheet for the draft in the editor.</summary>
   public void OpenPrepareCommand() {
      PrepareSteps.Clear();
      PrepareOwnerUser = null;
      PrepareOwnerPassword = null;
      PasswordReset?.Invoke();
      IsPrepareOpen = true;
   }

   /// <summary>Goes back from Prepare to the store editor.</summary>
   public void ClosePrepareCommand() {
      PrepareOwnerPassword = null;
      IsPrepareOpen = false;
   }

   /// <summary>Runs Prepare with the owner credential.</summary>
   public Task RunPrepareCommand() => RunAsync("Preparing the store...", async () => {
      var result = await Service.PostGetMeta_LogStorePrepare(new LogStorePrepareRequest {
         Store = CollectDraft(), OwnerUser = string.IsNullOrWhiteSpace(PrepareOwnerUser) ? null : PrepareOwnerUser.Trim(),
         OwnerPassword = PrepareOwnerPassword, CreateDatabase = PrepareCreateDatabase, GrantRuntimeUser = PrepareGrantRuntime
      });
      PrepareSteps.Clear();
      foreach (var step in result.Steps) PrepareSteps.Add(step);
      PrepareOwnerPassword = null;
      Status = result.Success ? "The store is prepared. Apply it and Save the list." : "Prepare did not finish; see the steps.";
   });

   #endregion

   #region Retention

   /// <summary>Rules being edited.</summary>
   public LogRetentionRules? Retention { get => Get<LogRetentionRules>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>Audit group rows.</summary>
   public ObservableCollection<LogAuditRetentionRow> RetentionGroups { get; } = [];

   /// <summary>Telemetry size limit as typed; empty = no limit.</summary>
   public string RetentionMaxGb { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Last run caption.</summary>
   public string RetentionLastRun { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Preview result.</summary>
   public ObservableCollection<LogRetentionPreviewItem> RetentionPreview { get; } = [];

   private async Task LoadRetentionAsync() => ShowRetention(await Service.GetMeta_LogRetention());

   private void ShowRetention(LogRetentionView view) {
      Retention = view.Rules;
      RetentionMaxGb = view.Rules.TelemetryMaxGb?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";
      RetentionGroups.Clear();
      foreach (var a in view.Rules.Audit) RetentionGroups.Add(LogAuditRetentionRow.From(a));
      RetentionLastRun = view.LastRunUtc is { } t ? $"Last run {Local(t)}: {view.LastRunSummary}" : "The retention job has not run yet.";
      NotifyChanged(nameof(Retention));
   }

   private LogRetentionRules CollectRetention() {
      var r = Retention ?? new LogRetentionRules();
      r.TelemetryMaxGb = string.IsNullOrWhiteSpace(RetentionMaxGb) ? null
         : double.TryParse(RetentionMaxGb, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var gb) ? gb
         : throw new ActionException("The size limit must be a number of GB, or empty for no limit.", 400);
      r.Audit = [.. RetentionGroups.Select(g => g.ToRule())];
      return r;
   }

   /// <summary>Counts what the rules on screen would delete now.</summary>
   public Task PreviewRetentionCommand() => RunAsync("Counting...", async () => {
      var preview = await Service.PostGetMeta_LogRetentionPreview(CollectRetention());
      RetentionPreview.Clear();
      foreach (var item in preview.Items) RetentionPreview.Add(item);
      Status = Warn(preview.Warnings, $"{preview.Items.Sum(i => i.Rows):N0} rows would be deleted now.");
   });

   /// <summary>Saves the rules; they apply from the next run.</summary>
   public Task SaveRetentionCommand() => RunAsync("Saving retention...", async () => {
      ShowRetention(await Service.PostGetMeta_LogRetentionSave(CollectRetention()));
      Status = "Retention saved; it applies from the next daily run.";
   });

   #endregion
}
