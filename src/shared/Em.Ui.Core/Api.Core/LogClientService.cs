using Em.Api.Core.Models;
using Em.Shared;
using Em.Ui.Core.Shared;

namespace Em.Ui.Core;

/// <summary>Shared proxy of the log engine actions for WPF, MAUI and other UI hosts.</summary>
[Module(Defaults.LogModuleName)]
public sealed class LogClientService(IEmApp app, Func<ApiClient?> activeClient) : ServiceUiBase(app), ILogServices
{
   private void Prepare() => ApiClient = activeClient();

   /// <inheritdoc />
   public Task PostMeta_LogIngest(LogIngestBatch batch) { Prepare(); return PostAsync(nameof(PostMeta_LogIngest), batch); }

   /// <inheritdoc />
   public Task<LogSummary> GetMeta_LogSummary(DateTime from, DateTime to) { Prepare(); return GetAsync<LogSummary>(nameof(GetMeta_LogSummary), from, to); }

   /// <inheritdoc />
   public Task<LogEventPage> GetMeta_LogEvents(DateTime from, DateTime to, LogEventKind? kind, LogEventLevel? minLevel, string? category,
      string? text, string? requestId, string? userId, string? cursor, int take) {
      Prepare();
      return GetAsync<LogEventPage>(nameof(GetMeta_LogEvents), from, to, kind!, minLevel!, category!, text!, requestId!, userId!, cursor!, take);
   }

   /// <inheritdoc />
   public Task<LogErrorPage> GetMeta_LogErrors(DateTime from, DateTime to, LogErrorState? state, string? cursor, int take) {
      Prepare();
      return GetAsync<LogErrorPage>(nameof(GetMeta_LogErrors), from, to, state!, cursor!, take);
   }

   /// <inheritdoc />
   public Task<LogErrorDetail> GetMeta_LogErrorDetail(string fingerprint) { Prepare(); return GetAsync<LogErrorDetail>(nameof(GetMeta_LogErrorDetail), fingerprint); }

   /// <inheritdoc />
   public Task PostMeta_LogErrorState(string fingerprint, LogErrorState state) { Prepare(); return PostAsync(nameof(PostMeta_LogErrorState), fingerprint, state); }

   /// <inheritdoc />
   public Task<AuditActivityPage> GetMeta_LogAudit(DateTime from, DateTime to, string? group, string? kind, string? targetKey, string? docNo,
      string? actorUserId, string? sessionId, string? cursor, int take) {
      Prepare();
      return GetAsync<AuditActivityPage>(nameof(GetMeta_LogAudit), from, to, group!, kind!, targetKey!, docNo!, actorUserId!, sessionId!, cursor!, take);
   }

   /// <inheritdoc />
   public Task<LogSignInFailureGroup[]> GetMeta_LogSignInFailures(DateTime from, DateTime to) {
      Prepare(); return GetAsync<LogSignInFailureGroup[]>(nameof(GetMeta_LogSignInFailures), from, to);
   }

   /// <inheritdoc />
   public Task<AuditCatalogGroup[]> GetMeta_AuditCatalog() { Prepare(); return GetAsync<AuditCatalogGroup[]>(nameof(GetMeta_AuditCatalog)); }

   /// <inheritdoc />
   public Task<LogStoreSettingsView> GetMeta_LogStores() { Prepare(); return GetAsync<LogStoreSettingsView>(nameof(GetMeta_LogStores)); }

   /// <inheritdoc />
   public Task<LogStoreTestResult> PostGetMeta_LogStoreTest(LogStoreDraft store) {
      Prepare(); return PostAsync<LogStoreTestResult>(TimeSpan.FromSeconds(60), nameof(PostGetMeta_LogStoreTest), store);
   }

   /// <inheritdoc />
   public Task<LogPrepareResult> PostGetMeta_LogStorePrepare(LogStorePrepareRequest request) {
      Prepare(); return PostAsync<LogPrepareResult>(TimeSpan.FromMinutes(5), nameof(PostGetMeta_LogStorePrepare), request);
   }

   /// <inheritdoc />
   public Task<LogStoreSettingsView> PostGetMeta_LogStoreSave(LogStoreSave request) {
      Prepare(); return PostAsync<LogStoreSettingsView>(TimeSpan.FromSeconds(120), nameof(PostGetMeta_LogStoreSave), request);
   }

   /// <inheritdoc />
   public Task<LogRetentionView> GetMeta_LogRetention() { Prepare(); return GetAsync<LogRetentionView>(nameof(GetMeta_LogRetention)); }

   /// <inheritdoc />
   public Task<LogRetentionPreview> PostGetMeta_LogRetentionPreview(LogRetentionRules rules) {
      Prepare(); return PostAsync<LogRetentionPreview>(TimeSpan.FromMinutes(5), nameof(PostGetMeta_LogRetentionPreview), rules);
   }

   /// <inheritdoc />
   public Task<LogRetentionView> PostGetMeta_LogRetentionSave(LogRetentionRules rules) {
      Prepare(); return PostAsync<LogRetentionView>(nameof(PostGetMeta_LogRetentionSave), rules);
   }
}

/// <summary>Shared proxy of the audit activity actions for module screens.</summary>
[Module(Defaults.AuditModuleName)]
public sealed class AuditClientService(IEmApp app, Func<ApiClient?> activeClient) : ServiceUiBase(app), IAuditServices
{
   private void Prepare() => ApiClient = activeClient();

   /// <inheritdoc />
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByDoc(string group, string key, string? cursor, int take) {
      Prepare(); return GetAsync<AuditActivityPage>(nameof(GetMeta_AuditActivity_ByDoc), group, key, cursor!, take);
   }

   /// <inheritdoc />
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByDocNo(string group, string docNo, string? cursor, int take) {
      Prepare(); return GetAsync<AuditActivityPage>(nameof(GetMeta_AuditActivity_ByDocNo), group, docNo, cursor!, take);
   }

   /// <inheritdoc />
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByType(string group, DateTime? from, DateTime? to, string? userId, string? kinds,
      string? cursor, int take) {
      Prepare(); return GetAsync<AuditActivityPage>(nameof(GetMeta_AuditActivity_ByType), group, from!, to!, userId!, kinds!, cursor!, take);
   }

   /// <inheritdoc />
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByTarget(string targetType, string key, string groups, string? cursor, int take) {
      Prepare(); return GetAsync<AuditActivityPage>(nameof(GetMeta_AuditActivity_ByTarget), targetType, key, groups, cursor!, take);
   }

   /// <inheritdoc />
   public Task<AuditActivity[]> GetMeta_AuditActivity_Since(string group, string? key, DateTime afterTime, string? afterId) {
      Prepare(); return GetAsync<AuditActivity[]>(nameof(GetMeta_AuditActivity_Since), group, key!, afterTime, afterId!);
   }

   /// <inheritdoc />
   public Task PostMeta_AuditEntry(string group, string key, string kind, string? docNo, string? detail) {
      Prepare(); return PostAsync(nameof(PostMeta_AuditEntry), group, key, kind, docNo!, detail!);
   }
}
