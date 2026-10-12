using System.Runtime.CompilerServices;
using Em.Api.Core.Models;
using Em.Shared;
using Em.Ui.Wpf.Navigations;

namespace Em.Ui.Wpf.Core.Tests;

public class LogManagerTests
{
   // Loads XAML resources only: no window, layout render, screenshot or live application.
   [WpfFact]
   public void ManagerXamlLoadsWithoutRendering() {
      var app = (EmApp)RuntimeHelpers.GetUninitializedObject(typeof(EmApp));
      var view = new LogManager(app);
      Assert.IsType<System.Windows.Style>(view.Resources["logRowStyle"]);
      Assert.IsType<System.Windows.Controls.PasswordBox>(view.FindName("storePassword"));
      Assert.False(view.Vm.IsSheetOpen);
   }

   [Fact]
   public async Task TabsFollowTheClaims() {
      var vm = new LogManagerVm(new StubLogService(), claim => claim is ILogServices.TelemetryReadClaim or ILogServices.AuditReadClaim);
      await vm.ReloadAsync();
      Assert.Equal(["summary", "audit", "errors", "log"], vm.Tabs.Select(t => t.Key));
      Assert.True(vm.IsSummaryTab);
      Assert.Contains(vm.SummaryLines, l => l.Caption == "Errors" && l.Value == "3");
   }

   [Fact]
   public async Task AuditPagesWithTheCursorAndOpensTheDetail() {
      var service = new StubLogService();
      var vm = new LogManagerVm(service, _ => true);
      await vm.ReloadAsync();
      vm.ActiveTab = vm.Tabs.Single(t => t.Key == "audit");
      await vm.SearchAuditCommand();
      Assert.Single(vm.AuditRows);
      Assert.True(vm.HasMoreAudit);
      await vm.MoreAuditCommand();
      Assert.Equal(2, vm.AuditRows.Count);
      Assert.False(vm.HasMoreAudit);
      Assert.Equal("next", service.LastCursor);

      vm.SelectedAudit = vm.AuditRows[0];
      vm.OpenAuditCommand();
      Assert.True(vm.IsAuditSheetOpen);
      Assert.Single(vm.AuditChanges);
      Assert.Contains(vm.AuditDetail, l => l.Caption == "Reason" && l.Value == "typo");
      vm.ViewRequestCommand();
      Assert.True(vm.IsLogTab);
      Assert.Equal("srv:0001A:1", vm.EventRequestId);
      Assert.False(vm.IsAuditSheetOpen);
   }

   [Fact]
   public async Task RetentionRowsTurnEmptyIntoNever() {
      var service = new StubLogService();
      var vm = new LogManagerVm(service, _ => true);
      await vm.ReloadAsync();
      vm.ActiveTab = vm.Tabs.Single(t => t.Key == "settings");
      await vm.RefreshCommand();
      Assert.Equal(2, vm.RetentionGroups.Count);
      vm.RetentionGroups.Single(g => g.Group == "Invoice").Months = "";
      vm.RetentionMaxGb = "";
      await vm.SaveRetentionCommand();
      Assert.Null(service.SavedRules!.Audit.Single(a => a.Group == "Invoice").Months);
      Assert.Null(service.SavedRules.TelemetryMaxGb);
   }

   [Fact]
   public async Task ActivityLoadsOnFirstSelectionAndExplainsAMissingLogEngine() {
      var audit = new StubAuditService();
      var vm = new UserActivityVm(audit);
      vm.Reset("U1");
      Assert.Equal(0, audit.Calls);
      vm.IsActive = true;
      await Task.Delay(50);
      Assert.Equal(1, audit.Calls);
      Assert.Single(vm.Items);
      Assert.True(vm.Items[0].IsDanger);

      audit.Fail = new ActionException("not found", 404);
      vm.Reset("U2");
      await Task.Delay(50);
      Assert.Contains("log engine is off", vm.Message);
   }

   private sealed class StubAuditService : IAuditServices
   {
      public int Calls { get; private set; }
      public ActionException? Fail { get; set; }
      public Task<AuditActivityPage> GetMeta_AuditActivity_ByTarget(string targetType, string key, string groups, string? cursor, int take) {
         Calls++;
         if (Fail is { } fail) throw fail;
         return Task.FromResult(new AuditActivityPage { Items = [new AuditActivity { Id = "1", Group = "System.Auth", Kind = "signin.failed", Time = DateTime.UtcNow, ActorType = AuditActorTypes.Anonymous }] });
      }
      public Task<AuditActivityPage> GetMeta_AuditActivity_ByDoc(string group, string key, string? cursor, int take) => throw new NotSupportedException();
      public Task<AuditActivityPage> GetMeta_AuditActivity_ByDocNo(string group, string docNo, string? cursor, int take) => throw new NotSupportedException();
      public Task<AuditActivityPage> GetMeta_AuditActivity_ByType(string group, DateTime? from, DateTime? to, string? userId, string? kinds, string? cursor, int take) => throw new NotSupportedException();
      public Task<AuditActivity[]> GetMeta_AuditActivity_Since(string group, string? key, DateTime afterTime, string? afterId) => throw new NotSupportedException();
      public Task PostMeta_AuditEntry(string group, string key, string kind, string? docNo, string? detail) => throw new NotSupportedException();
   }

   private sealed class StubLogService : ILogServices
   {
      public string? LastCursor { get; private set; }
      public LogRetentionRules? SavedRules { get; private set; }

      public Task PostMeta_LogIngest(LogIngestBatch batch) => Task.CompletedTask;
      public Task<LogSummary> GetMeta_LogSummary(DateTime from, DateTime to) =>
         Task.FromResult(new LogSummary { Errors = 3, StoreName = "Internal", StoreProvider = "MicrosoftSqlServer", StoreHealthy = true });
      public Task<LogEventPage> GetMeta_LogEvents(DateTime from, DateTime to, LogEventKind? kind, LogEventLevel? minLevel, string? category, string? text, string? requestId, string? userId, string? cursor, int take) =>
         Task.FromResult(new LogEventPage { Items = [new LogEvent { Time = DateTime.UtcNow, Message = "line", Category = "c", RequestId = requestId }] });
      public Task<LogErrorPage> GetMeta_LogErrors(DateTime from, DateTime to, LogErrorState? state, string? cursor, int take) => Task.FromResult(new LogErrorPage());
      public Task<LogErrorDetail> GetMeta_LogErrorDetail(string fingerprint) => Task.FromResult(new LogErrorDetail());
      public Task PostMeta_LogErrorState(string fingerprint, LogErrorState state) => Task.CompletedTask;
      public Task<AuditActivityPage> GetMeta_LogAudit(DateTime from, DateTime to, string? group, string? kind, string? targetKey, string? docNo, string? actorUserId, string? sessionId, string? cursor, int take) {
         LastCursor = cursor;
         var item = new AuditActivity {
            Id = cursor is null ? "2" : "1", Group = "Invoice", Kind = "updated", Time = DateTime.UtcNow, ActorAccount = "budi", Reason = "typo",
            RequestId = "srv:0001A:1", Changes = [new AuditFieldChange { Field = "cInvoiceNo", Label = "Number", Old = "A", New = "B" }]
         };
         return Task.FromResult(new AuditActivityPage { Items = [item], NextCursor = cursor is null ? "next" : null });
      }
      public Task<LogSignInFailureGroup[]> GetMeta_LogSignInFailures(DateTime from, DateTime to) => Task.FromResult<LogSignInFailureGroup[]>([]);
      public Task<AuditCatalogGroup[]> GetMeta_AuditCatalog() => Task.FromResult<AuditCatalogGroup[]>([new AuditCatalogGroup { Name = "Invoice", Title = "Invoice" }]);
      public Task<LogStoreSettingsView> GetMeta_LogStores() => Task.FromResult(new LogStoreSettingsView {
         Stores = [new LogStoreInfo { Id = "I", Name = "Internal", Kind = LogStoreKind.Internal, Role = LogStoreRole.Write }],
         Active = [new LogStoreInfo { Id = "I", Name = "Internal", Kind = LogStoreKind.Internal, Role = LogStoreRole.Write }]
      });
      public Task<LogStoreTestResult> PostGetMeta_LogStoreTest(LogStoreDraft store) => Task.FromResult(new LogStoreTestResult { Message = "ok" });
      public Task<LogPrepareResult> PostGetMeta_LogStorePrepare(LogStorePrepareRequest request) => Task.FromResult(new LogPrepareResult { Success = true });
      public Task<LogStoreSettingsView> PostGetMeta_LogStoreSave(LogStoreSave request) => GetMeta_LogStores();
      public Task<LogRetentionView> GetMeta_LogRetention() => Task.FromResult(new LogRetentionView { Rules = new LogRetentionRules {
         Audit = [new LogAuditRetention { Group = "System.Auth", IsSystem = true, Months = 24, MinMonths = 12 }, new LogAuditRetention { Group = "Invoice", Months = 120 }]
      } });
      public Task<LogRetentionPreview> PostGetMeta_LogRetentionPreview(LogRetentionRules rules) => Task.FromResult(new LogRetentionPreview());
      public Task<LogRetentionView> PostGetMeta_LogRetentionSave(LogRetentionRules rules) {
         SavedRules = rules;
         return Task.FromResult(new LogRetentionView { Rules = rules });
      }
   }
}
