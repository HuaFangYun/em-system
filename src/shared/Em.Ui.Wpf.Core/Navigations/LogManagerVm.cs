using System.Collections.ObjectModel;
using System.Text.Json;
using Em.Api.Core.Models;
using Em.Shared;
using Em.Ui.Wpf.Core;
using Em.Ui.Wpf.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Ui.Wpf.Navigations;

/// <summary>One panel of the Log Manager tab strip.</summary>
/// <param name="Key">Stable key.</param>
/// <param name="Title">Caption.</param>
public sealed record LogTab(string Key, string Title);

/// <summary>A choice with its caption, for combo boxes.</summary>
/// <typeparam name="T">Value type.</typeparam>
/// <param name="Value">Value.</param>
/// <param name="Caption">Caption shown in the list.</param>
public sealed record LogOption<T>(T Value, string Caption);

/// <summary>One line of the summary tab; <see cref="TargetTab"/> is the tab the line opens.</summary>
/// <param name="Caption">Caption.</param>
/// <param name="Value">Value text.</param>
/// <param name="TargetTab">Tab key, or <c>null</c> when the line opens nothing.</param>
public sealed record LogSummaryLine(string Caption, string Value, string? TargetTab)
{
   /// <summary>The line opens a tab.</summary>
   public bool CanOpen => TargetTab is not null;
}

/// <summary>An audit entry with its display texts.</summary>
public sealed class LogAuditRow(AuditActivity entry)
{
   /// <summary>The entry.</summary>
   public AuditActivity Entry { get; } = entry;
   /// <summary>Local time.</summary>
   public string Time => LogManagerVm.Local(Entry.Time);
   /// <summary>Group.</summary>
   public string Group => Entry.Group;
   /// <summary>Kind label, or the kind.</summary>
   public string Kind => Entry.KindLabel ?? Entry.Kind;
   /// <summary>Actor caption.</summary>
   public string Actor => LogManagerVm.ActorText(Entry);
   /// <summary>Target caption (document number or key).</summary>
   public string Target => Entry.DocNo ?? Entry.TargetKey ?? "";
   /// <summary>Summary.</summary>
   public string Summary => Entry.Summary ?? "";
   /// <summary>Reported by a client rather than proven by the server.</summary>
   public bool FromClient => Entry.Source == "client";
}

/// <summary>A telemetry event with its display texts.</summary>
public sealed class LogEventRow(LogEvent e)
{
   /// <summary>The event.</summary>
   public LogEvent Event { get; } = e;
   /// <summary>Local time.</summary>
   public string Time => LogManagerVm.Local(Event.Time);
   /// <summary>Level caption.</summary>
   public string Level => Event.Level.ToString();
   /// <summary>Category.</summary>
   public string Category => Event.Category;
   /// <summary>Message as the console shows it.</summary>
   public string Message => Event.Message;
   /// <summary>User caption.</summary>
   public string User => Event.Account ?? Event.UserId ?? "";
   /// <summary>Source caption (server or the client application).</summary>
   public string Source => Event.Source == "client" ? $"client {Event.App}" : "server";
}

/// <summary>An error group with its display texts.</summary>
public sealed class LogErrorRow(LogErrorGroup group)
{
   /// <summary>The group.</summary>
   public LogErrorGroup Group { get; } = group;
   /// <summary>Last seen, local time.</summary>
   public string LastSeen => LogManagerVm.Local(Group.LastSeen);
   /// <summary>Exception type.</summary>
   public string Type => Group.ExceptionType;
   /// <summary>Top application frame.</summary>
   public string Frame => Group.TopFrame ?? "";
   /// <summary>Count.</summary>
   public string Count => Group.Count.ToString("N0");
   /// <summary>Affected users.</summary>
   public string Users => Group.AffectedUsers.ToString("N0");
   /// <summary>State.</summary>
   public string State => Group.State.ToString();
}

/// <summary>
/// State and commands of the Log Manager: summary, audit, security, errors, server log and settings (database and
/// retention). Tabs follow the caller's claims; every list pages with an opaque cursor and a Load more button.
/// </summary>
public sealed partial class LogManagerVm : MvvmModelBase
{
   internal const string SummaryTab = "summary";
   internal const string AuditTab = "audit";
   internal const string SecurityTab = "security";
   internal const string ErrorsTab = "errors";
   internal const string LogTabKey = "log";
   internal const string SettingsTab = "settings";
   private const int PageSize = 100;

   private readonly ILogServices? _service;
   private readonly Func<string, bool>? _hasClaim;
   private string? _auditCursor;
   private string? _eventCursor;
   private string? _errorCursor;
   private string? _securityCursor;

   /// <summary>Creates a view model for navigation; the service comes from the application.</summary>
   public LogManagerVm() { RegisterCommands(); }

   /// <summary>Creates a view model with an explicit service and claim check, for tests and other hosts.</summary>
   public LogManagerVm(ILogServices service, Func<string, bool> hasClaim) {
      _service = service;
      _hasClaim = hasClaim;
      RegisterCommands();
   }

   private ILogServices Service => _service ?? EmApp!.ServiceProvider.GetRequiredService<ILogServices>();

   /// <summary>Asks the user a yes/no question (message, title); <c>null</c> answers no.</summary>
   public Func<string, string, bool>? Confirm { get; set; }

   /// <summary>Panels the caller may open.</summary>
   public ObservableCollection<LogTab> Tabs { get; } = [];

   /// <summary>Selected panel.</summary>
   public LogTab? ActiveTab {
      get => Get<LogTab>();
      set => Set(value, tab => {
         foreach (var name in (string[])[nameof(IsSummaryTab), nameof(IsAuditTab), nameof(IsSecurityTab), nameof(IsErrorsTab), nameof(IsLogTab), nameof(IsSettingsTab)])
            NotifyChanged(name);
         if (IsLoaded) _ = LoadTabAsync();
      });
   }

   /// <summary>Summary panel shown.</summary>
   public bool IsSummaryTab => ActiveTab?.Key == SummaryTab;
   /// <summary>Audit panel shown.</summary>
   public bool IsAuditTab => ActiveTab?.Key == AuditTab;
   /// <summary>Security panel shown.</summary>
   public bool IsSecurityTab => ActiveTab?.Key == SecurityTab;
   /// <summary>Errors panel shown.</summary>
   public bool IsErrorsTab => ActiveTab?.Key == ErrorsTab;
   /// <summary>Log panel shown.</summary>
   public bool IsLogTab => ActiveTab?.Key == LogTabKey;
   /// <summary>Settings panel shown.</summary>
   public bool IsSettingsTab => ActiveTab?.Key == SettingsTab;

   /// <summary>Time ranges; every tab uses the selected one.</summary>
   public LogOption<TimeSpan>[] Ranges { get; } = [
      new(TimeSpan.FromHours(1), "Last hour"), new(TimeSpan.FromHours(24), "Last 24 hours"),
      new(TimeSpan.FromDays(7), "Last 7 days"), new(TimeSpan.FromDays(31), "Last 31 days")
   ];

   /// <summary>Selected range.</summary>
   public TimeSpan Range { get => Get<TimeSpan?>() ?? TimeSpan.FromHours(24); set => Set(value); }

   /// <summary>True after the first successful load.</summary>
   public bool IsLoaded { get => Get<bool>(); private set => Set(value); }

   /// <summary>Result of the last operation.</summary>
   public string Status { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Lists and toolbars can be used.</summary>
   public bool CanBrowse => IsNotBusy && !IsSheetOpen;

   /// <summary>Editor inputs can be changed.</summary>
   public bool CanEdit => IsNotBusy;

   /// <summary>A side sheet is open.</summary>
   public bool IsSheetOpen => IsAuditSheetOpen || IsEventSheetOpen || IsErrorSheetOpen || IsStoreEditorOpen || IsPrepareOpen;

   private void RegisterCommands() {
      RegisterCommand(nameof(RefreshCommand), RefreshCommand, () => IsNotBusy);
      RegisterCommand(nameof(CloseSheetCommand), CloseSheetCommand, () => IsNotBusy);
      RegisterCommand<LogSummaryLine>(nameof(OpenSummaryLineCommand), OpenSummaryLineCommand, l => l?.CanOpen == true);
      RegisterCommand(nameof(SearchAuditCommand), SearchAuditCommand, () => CanBrowse);
      RegisterCommand(nameof(MoreAuditCommand), MoreAuditCommand, () => CanBrowse && _auditCursor is not null);
      RegisterCommand(nameof(OpenAuditCommand), OpenAuditCommand, () => CanBrowse && SelectedAudit is not null);
      RegisterCommand(nameof(ViewRequestCommand), ViewRequestCommand, () => IsNotBusy && SelectedAudit?.Entry.RequestId is not null);
      RegisterCommand(nameof(SearchSecurityCommand), SearchSecurityCommand, () => CanBrowse);
      RegisterCommand(nameof(MoreSecurityCommand), MoreSecurityCommand, () => CanBrowse && _securityCursor is not null);
      RegisterCommand(nameof(SearchErrorsCommand), SearchErrorsCommand, () => CanBrowse);
      RegisterCommand(nameof(MoreErrorsCommand), MoreErrorsCommand, () => CanBrowse && _errorCursor is not null);
      RegisterCommand(nameof(OpenErrorCommand), OpenErrorCommand, () => CanBrowse && SelectedError is not null);
      RegisterCommand<LogErrorState>(nameof(SetErrorStateCommand), SetErrorStateCommand, _ => IsNotBusy && ErrorDetail is not null);
      RegisterCommand(nameof(SearchEventsCommand), SearchEventsCommand, () => CanBrowse);
      RegisterCommand(nameof(MoreEventsCommand), MoreEventsCommand, () => CanBrowse && _eventCursor is not null);
      RegisterCommand(nameof(OpenEventCommand), OpenEventCommand, () => CanBrowse && SelectedEvent is not null);
      RegisterCommand(nameof(FilterRequestCommand), FilterRequestCommand, () => IsNotBusy && SelectedEvent?.Event.RequestId is not null);
      RegisterSettingsCommands();
   }

   /// <summary>True when the caller holds a log claim (administrators and the debugger always do).</summary>
   public bool Has(string claim) {
      if (_hasClaim is not null) return _hasClaim(claim);
      if (EmApp is not { } app) return false;
      if (app.IsDebugBypass || app.ActiveUser?.cUserIsAdmin == true) return true;
      return app.ActiveUser?.AvailableClaims.Any(c => string.Equals(c.ModuleName, Defaults.LogModuleName, StringComparison.OrdinalIgnoreCase) &&
                                                      string.Equals(c.Name, claim, StringComparison.OrdinalIgnoreCase)) == true;
   }

   /// <summary>Builds the tab list from the claims, then loads the first tab.</summary>
   public async Task ReloadAsync() {
      if (Tabs.Count == 0) {
         if (Has(ILogServices.TelemetryReadClaim)) Tabs.Add(new LogTab(SummaryTab, "Summary"));
         if (Has(ILogServices.AuditReadClaim)) Tabs.Add(new LogTab(AuditTab, "Audit"));
         if (Has(ILogServices.SecurityReadClaim)) Tabs.Add(new LogTab(SecurityTab, "Security"));
         if (Has(ILogServices.TelemetryReadClaim)) {
            Tabs.Add(new LogTab(ErrorsTab, "Errors"));
            Tabs.Add(new LogTab(LogTabKey, "Log"));
         }
         if (Has(ILogServices.SettingsClaim)) Tabs.Add(new LogTab(SettingsTab, "Settings"));
         if (Tabs.Count == 0) {
            Status = "You may open the Log Manager, but no log claim lets you read anything yet.";
            return;
         }
         ActiveTab = Tabs[0];
      }
      if (await RunAsync("Loading...", async () => {
             if (Groups.Count == 0) await LoadCatalogAsync();
             await LoadTabCoreAsync();
          })) {
         IsLoaded = true;
      }
   }

   /// <summary>Reloads the current tab.</summary>
   public Task RefreshCommand() => ReloadAsync();

   private Task LoadTabAsync() => RunAsync("Loading...", LoadTabCoreAsync);

   private Task LoadTabCoreAsync() => ActiveTab?.Key switch {
      SummaryTab => LoadSummaryAsync(),
      AuditTab => LoadAuditAsync(false),
      SecurityTab => LoadSecurityAsync(false),
      ErrorsTab => LoadErrorsAsync(false),
      LogTabKey => LoadEventsAsync(false),
      SettingsTab => LoadSettingsAsync(),
      _ => Task.CompletedTask
   };

   private (DateTime From, DateTime To) Window() {
      var to = DateTime.UtcNow.AddMinutes(1);
      return (to - Range, to);
   }

   #region Summary

   /// <summary>Summary lines.</summary>
   public ObservableCollection<LogSummaryLine> SummaryLines { get; } = [];

   private async Task LoadSummaryAsync() {
      var (from, to) = Window();
      var s = await Service.GetMeta_LogSummary(from, to);
      SummaryLines.Clear();
      SummaryLines.Add(new LogSummaryLine("Errors", s.Errors.ToString("N0"), LogTabKey));
      SummaryLines.Add(new LogSummaryLine("Warnings", s.Warnings.ToString("N0"), LogTabKey));
      SummaryLines.Add(new LogSummaryLine("New error groups", s.NewErrorGroups.ToString("N0"), ErrorsTab));
      SummaryLines.Add(new LogSummaryLine("Open error groups", s.OpenErrorGroups.ToString("N0"), ErrorsTab));
      SummaryLines.Add(new LogSummaryLine("Requests", s.Requests.ToString("N0"), LogTabKey));
      if (s.AuditEntries is { } audit) SummaryLines.Add(new LogSummaryLine("Audit entries", audit.ToString("N0"), Tabs.Any(t => t.Key == AuditTab) ? AuditTab : null));
      if (s.FailedSignIns is { } failed) SummaryLines.Add(new LogSummaryLine("Failed sign-ins", failed.ToString("N0"), Tabs.Any(t => t.Key == SecurityTab) ? SecurityTab : null));
      SummaryLines.Add(new LogSummaryLine("Events dropped (queue full)", s.DroppedEvents.ToString("N0"), null));
      SummaryLines.Add(new LogSummaryLine("Audit entries waiting to move", s.AuditOutboxPending.ToString("N0"), null));
      SummaryLines.Add(new LogSummaryLine("Disk segments waiting", s.PendingSegments.ToString("N0"), null));
      SummaryLines.Add(new LogSummaryLine("Write store", $"{s.StoreName} ({s.StoreProvider})", Tabs.Any(t => t.Key == SettingsTab) ? SettingsTab : null));
      SummaryLines.Add(new LogSummaryLine("Store state", s.StoreHealthy ? "Healthy" : $"Unhealthy: {s.StoreMessage}", null));
      Status = "Summary loaded.";
   }

   /// <summary>Opens the tab a summary line points to.</summary>
   public void OpenSummaryLineCommand(LogSummaryLine line) {
      if (Tabs.FirstOrDefault(t => t.Key == line.TargetTab) is { } tab) ActiveTab = tab;
   }

   #endregion

   #region Audit

   /// <summary>Audit groups (first entry = every group).</summary>
   public ObservableCollection<LogOption<string?>> Groups { get; } = [];

   /// <summary>Selected group filter.</summary>
   public string? AuditGroup { get => Get<string>(); set => Set(value); }
   /// <summary>Kind filter.</summary>
   public string? AuditKind { get => Get<string>(); set => Set(value); }
   /// <summary>Target key filter.</summary>
   public string? AuditKey { get => Get<string>(); set => Set(value); }
   /// <summary>Document number filter.</summary>
   public string? AuditDocNo { get => Get<string>(); set => Set(value); }
   /// <summary>Actor user id filter.</summary>
   public string? AuditUser { get => Get<string>(); set => Set(value); }
   /// <summary>Session id filter.</summary>
   public string? AuditSession { get => Get<string>(); set => Set(value); }

   /// <summary>Audit rows.</summary>
   public ObservableCollection<LogAuditRow> AuditRows { get; } = [];

   /// <summary>Selected audit row.</summary>
   public LogAuditRow? SelectedAudit { get => Get<LogAuditRow>(); set => Set(value, _ => UpdateState()); }

   /// <summary>The audit detail sheet is open.</summary>
   public bool IsAuditSheetOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>More audit rows can be loaded.</summary>
   public bool HasMoreAudit => _auditCursor is not null;

   /// <summary>Field changes of the selected entry.</summary>
   public ObservableCollection<AuditFieldChange> AuditChanges { get; } = [];

   /// <summary>Formatted snapshot of the selected entry.</summary>
   public string AuditSnapshot { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Detail lines of the selected entry.</summary>
   public ObservableCollection<LogSummaryLine> AuditDetail { get; } = [];

   private async Task LoadCatalogAsync() {
      Groups.Clear();
      Groups.Add(new LogOption<string?>(null, "All groups"));
      try {
         foreach (var g in await Service.GetMeta_AuditCatalog()) Groups.Add(new LogOption<string?>(g.Name, $"{g.Title} ({g.Name})"));
      }
      catch (ActionException) {
         // Without the catalog the group filter only offers "All groups".
      }
   }

   /// <summary>Searches with the current filters.</summary>
   public Task SearchAuditCommand() => RunAsync("Searching audit...", () => LoadAuditAsync(false));

   /// <summary>Loads the next page.</summary>
   public Task MoreAuditCommand() => RunAsync("Loading more...", () => LoadAuditAsync(true));

   private async Task LoadAuditAsync(bool more) {
      var (from, to) = Window();
      var page = await Service.GetMeta_LogAudit(from, to, Empty(AuditGroup), Empty(AuditKind), Empty(AuditKey), Empty(AuditDocNo),
         Empty(AuditUser), Empty(AuditSession), more ? _auditCursor : null, PageSize);
      if (!more) AuditRows.Clear();
      foreach (var item in page.Items) AuditRows.Add(new LogAuditRow(item));
      _auditCursor = page.NextCursor;
      NotifyChanged(nameof(HasMoreAudit));
      Status = Warn(page.Warnings, $"{AuditRows.Count:N0} audit entries.");
   }

   /// <summary>Opens the selected entry in the side sheet.</summary>
   public void OpenAuditCommand() {
      if (SelectedAudit is not { } row) return;
      var e = row.Entry;
      AuditDetail.Clear();
      AuditDetail.Add(new LogSummaryLine("Time", row.Time, null));
      AuditDetail.Add(new LogSummaryLine("Event", $"{e.Group} / {row.Kind}", null));
      AuditDetail.Add(new LogSummaryLine("Actor", row.Actor, null));
      AuditDetail.Add(new LogSummaryLine("Target", $"{e.TargetType} {e.TargetKey}".Trim(), null));
      if (e.DocNo is not null) AuditDetail.Add(new LogSummaryLine("Document", e.DocNo, null));
      if (e.Summary is not null) AuditDetail.Add(new LogSummaryLine("Summary", e.Summary, null));
      if (e.Reason is not null) AuditDetail.Add(new LogSummaryLine("Reason", e.Reason, null));
      AuditDetail.Add(new LogSummaryLine("Source", e.Source == "client" ? "Reported by the client" : "Recorded by the server", null));
      if (e.ClientAddress is not null) AuditDetail.Add(new LogSummaryLine("Address", e.ClientAddress, null));
      if (e.SessionId is not null) AuditDetail.Add(new LogSummaryLine("Session", e.SessionId, null));
      if (e.Detail is not null) AuditDetail.Add(new LogSummaryLine("Detail", Pretty(e.Detail), null));
      AuditChanges.Clear();
      foreach (var c in e.Changes) AuditChanges.Add(c);
      AuditSnapshot = e.Snapshot is null ? "" : Pretty(e.Snapshot);
      IsAuditSheetOpen = true;
   }

   /// <summary>Shows the server log lines of the entry's request in the Log tab.</summary>
   public void ViewRequestCommand() {
      if (SelectedAudit?.Entry.RequestId is not { } id || Tabs.FirstOrDefault(t => t.Key == LogTabKey) is not { } tab) return;
      CloseSheetCommand();
      EventRequestId = id;
      EventKind = null;
      ActiveTab = tab;
   }

   #endregion

   #region Security

   /// <summary>Security groups.</summary>
   public LogOption<string>[] SecurityGroups { get; } = [new("System.Auth", "Sign-in and sessions"), new("System.Password", "Passwords")];

   /// <summary>Selected security group.</summary>
   public string SecurityGroup { get => Get<string>() ?? "System.Auth"; set => Set(value); }

   /// <summary>Security entries.</summary>
   public ObservableCollection<LogAuditRow> SecurityRows { get; } = [];

   /// <summary>Failed sign-ins grouped per account and address per five minutes.</summary>
   public ObservableCollection<LogSignInFailureGroup> SignInFailures { get; } = [];

   /// <summary>More security rows can be loaded.</summary>
   public bool HasMoreSecurity => _securityCursor is not null;

   /// <summary>Searches security entries.</summary>
   public Task SearchSecurityCommand() => RunAsync("Searching...", () => LoadSecurityAsync(false));

   /// <summary>Loads the next page.</summary>
   public Task MoreSecurityCommand() => RunAsync("Loading more...", () => LoadSecurityAsync(true));

   private async Task LoadSecurityAsync(bool more) {
      var (from, to) = Window();
      var page = await Service.GetMeta_LogAudit(from, to, SecurityGroup, null, null, null, null, null, more ? _securityCursor : null, PageSize);
      if (!more) {
         SecurityRows.Clear();
         SignInFailures.Clear();
         foreach (var g in await Service.GetMeta_LogSignInFailures(from, to)) SignInFailures.Add(g);
      }
      foreach (var item in page.Items) SecurityRows.Add(new LogAuditRow(item));
      _securityCursor = page.NextCursor;
      NotifyChanged(nameof(HasMoreSecurity));
      Status = Warn(page.Warnings, $"{SecurityRows.Count:N0} security entries, {SignInFailures.Sum(f => f.Count):N0} failed sign-ins.");
   }

   #endregion

   #region Errors

   /// <summary>Error state filters.</summary>
   public LogOption<LogErrorState?>[] ErrorStates { get; } = [
      new(null, "All states"), new(LogErrorState.New, "New"), new(LogErrorState.Acknowledged, "Acknowledged"),
      new(LogErrorState.Resolved, "Resolved"), new(LogErrorState.Ignored, "Ignored")
   ];

   /// <summary>Selected state filter.</summary>
   public LogErrorState? ErrorState { get => Get<LogErrorState?>(); set => Set(value); }

   /// <summary>Error groups.</summary>
   public ObservableCollection<LogErrorRow> ErrorRows { get; } = [];

   /// <summary>Selected group.</summary>
   public LogErrorRow? SelectedError { get => Get<LogErrorRow>(); set => Set(value, _ => UpdateState()); }

   /// <summary>Detail of the open group.</summary>
   public LogErrorDetail? ErrorDetail { get => Get<LogErrorDetail>(); private set => Set(value); }

   /// <summary>Stack trace of the latest occurrence.</summary>
   public string ErrorStack { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Latest occurrences.</summary>
   public ObservableCollection<LogEventRow> ErrorRecent { get; } = [];

   /// <summary>The error sheet is open.</summary>
   public bool IsErrorSheetOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>More groups can be loaded.</summary>
   public bool HasMoreErrors => _errorCursor is not null;

   /// <summary>Searches error groups.</summary>
   public Task SearchErrorsCommand() => RunAsync("Searching errors...", () => LoadErrorsAsync(false));

   /// <summary>Loads the next page.</summary>
   public Task MoreErrorsCommand() => RunAsync("Loading more...", () => LoadErrorsAsync(true));

   private async Task LoadErrorsAsync(bool more) {
      var (from, to) = Window();
      var page = await Service.GetMeta_LogErrors(from, to, ErrorState, more ? _errorCursor : null, PageSize);
      if (!more) ErrorRows.Clear();
      foreach (var g in page.Items) ErrorRows.Add(new LogErrorRow(g));
      _errorCursor = page.NextCursor;
      NotifyChanged(nameof(HasMoreErrors));
      Status = $"{ErrorRows.Count:N0} error groups.";
   }

   /// <summary>Opens the selected group.</summary>
   public Task OpenErrorCommand() => RunAsync("Loading error...", async () => {
      if (SelectedError is not { } row) return;
      var detail = await Service.GetMeta_LogErrorDetail(row.Group.Fingerprint);
      ErrorDetail = detail;
      ErrorStack = detail.Recent.FirstOrDefault(r => r.Exception is not null)?.Exception ?? "";
      ErrorRecent.Clear();
      foreach (var e in detail.Recent) ErrorRecent.Add(new LogEventRow(e));
      IsErrorSheetOpen = true;
   });

   /// <summary>Changes the state of the open group.</summary>
   public Task SetErrorStateCommand(LogErrorState state) => RunAsync("Saving...", async () => {
      if (ErrorDetail is not { } detail) return;
      await Service.PostMeta_LogErrorState(detail.Group.Fingerprint, state);
      detail.Group.State = state;
      Status = $"Error group marked {state}.";
      await LoadErrorsAsync(false);
   });

   #endregion

   #region Log

   /// <summary>Kind filters.</summary>
   public LogOption<LogEventKind?>[] EventKinds { get; } = [
      new(null, "All kinds"), new(LogEventKind.Diagnostic, "Diagnostic"), new(LogEventKind.Error, "Error"), new(LogEventKind.Access, "Access")
   ];

   /// <summary>Level filters (minimum).</summary>
   public LogOption<LogEventLevel?>[] EventLevels { get; } = [
      new(null, "All levels"), new(LogEventLevel.Information, "Information and above"), new(LogEventLevel.Warning, "Warning and above"),
      new(LogEventLevel.Error, "Error and above")
   ];

   /// <summary>Kind filter.</summary>
   public LogEventKind? EventKind { get => Get<LogEventKind?>(); set => Set(value); }
   /// <summary>Minimum level filter.</summary>
   public LogEventLevel? EventLevel { get => Get<LogEventLevel?>(); set => Set(value); }
   /// <summary>Category prefix filter.</summary>
   public string? EventCategory { get => Get<string>(); set => Set(value); }
   /// <summary>Message text filter.</summary>
   public string? EventText { get => Get<string>(); set => Set(value); }
   /// <summary>Request id filter.</summary>
   public string? EventRequestId { get => Get<string>(); set => Set(value); }
   /// <summary>User id filter.</summary>
   public string? EventUser { get => Get<string>(); set => Set(value); }

   /// <summary>Events.</summary>
   public ObservableCollection<LogEventRow> EventRows { get; } = [];

   /// <summary>Selected event.</summary>
   public LogEventRow? SelectedEvent { get => Get<LogEventRow>(); set => Set(value, _ => UpdateState()); }

   /// <summary>Detail lines of the open event.</summary>
   public ObservableCollection<LogSummaryLine> EventDetail { get; } = [];

   /// <summary>Exception text of the open event.</summary>
   public string EventException { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>The event sheet is open.</summary>
   public bool IsEventSheetOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>More events can be loaded.</summary>
   public bool HasMoreEvents => _eventCursor is not null;

   /// <summary>Searches events.</summary>
   public Task SearchEventsCommand() => RunAsync("Searching log...", () => LoadEventsAsync(false));

   /// <summary>Loads the next page.</summary>
   public Task MoreEventsCommand() => RunAsync("Loading more...", () => LoadEventsAsync(true));

   private async Task LoadEventsAsync(bool more) {
      var (from, to) = Window();
      var page = await Service.GetMeta_LogEvents(from, to, EventKind, EventLevel, Empty(EventCategory), Empty(EventText), Empty(EventRequestId),
         Empty(EventUser), more ? _eventCursor : null, PageSize);
      if (!more) EventRows.Clear();
      foreach (var e in page.Items) EventRows.Add(new LogEventRow(e));
      _eventCursor = page.NextCursor;
      NotifyChanged(nameof(HasMoreEvents));
      Status = Warn(page.Warnings, $"{EventRows.Count:N0} events.");
   }

   /// <summary>Opens the selected event.</summary>
   public void OpenEventCommand() {
      if (SelectedEvent is not { } row) return;
      var e = row.Event;
      EventDetail.Clear();
      EventDetail.Add(new LogSummaryLine("Time", row.Time, null));
      EventDetail.Add(new LogSummaryLine("Level", $"{e.Level} ({e.Kind})", null));
      EventDetail.Add(new LogSummaryLine("Category", e.Category, null));
      EventDetail.Add(new LogSummaryLine("Message", e.Message, null));
      if (e.Template is not null) EventDetail.Add(new LogSummaryLine("Template", e.Template, null));
      EventDetail.Add(new LogSummaryLine("Source", $"{row.Source} {e.AppVersion} on {e.Instance}".Trim(), null));
      if (e.UserId is not null) EventDetail.Add(new LogSummaryLine("User", $"{e.Account} ({e.UserId})", null));
      if (e.RequestId is not null) EventDetail.Add(new LogSummaryLine("Request", e.RequestId, null));
      if (e.ClientAddress is not null) EventDetail.Add(new LogSummaryLine("Address", e.ClientAddress, null));
      if (e.Fingerprint is not null) EventDetail.Add(new LogSummaryLine("Fingerprint", e.Fingerprint, null));
      if (e.Properties is not null) EventDetail.Add(new LogSummaryLine("Properties", Pretty(e.Properties), null));
      EventException = e.Exception ?? "";
      IsEventSheetOpen = true;
   }

   /// <summary>Shows every line of the open event's request.</summary>
   public Task FilterRequestCommand() {
      if (SelectedEvent?.Event.RequestId is not { } id) return Task.CompletedTask;
      CloseSheetCommand();
      EventRequestId = id;
      EventKind = null;
      EventLevel = null;
      return SearchEventsCommand();
   }

   #endregion

   /// <summary>Closes any side sheet.</summary>
   public void CloseSheetCommand() {
      IsAuditSheetOpen = IsEventSheetOpen = IsErrorSheetOpen = false;
      CloseSettingsSheets();
      UpdateState();
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
      NotifyChanged(nameof(IsSheetOpen));
      NotifyChanged(nameof(CanBrowse));
      NotifyChanged(nameof(CanEdit));
      foreach (var command in Commands) command.RaiseCanExecuteChanged();
   }

   internal static string Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

   internal static string ActorText(AuditActivity e) => e.ActorType switch {
      AuditActorTypes.User => e.ActorAccount ?? e.ActorUserId ?? "user",
      AuditActorTypes.Debug => e.ActingAs is { } acting ? $"{e.ActorAccount} as {acting}" : e.ActorAccount ?? "debug",
      AuditActorTypes.Robot => $"robot {e.ActorAccount}",
      _ => e.ActorType
   };

   private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

   private static string Warn(string[] warnings, string text) => warnings.Length == 0 ? text : $"{text} Some data is not available: {string.Join("; ", warnings)}";

   internal static string Pretty(string json) {
      try {
         using var doc = JsonDocument.Parse(json);
         return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
      }
      catch (JsonException) {
         return json;
      }
   }
}
