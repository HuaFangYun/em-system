using Em.Shared;

namespace Em.Api.Core.Models;

/// <summary>
/// Log engine actions: client telemetry ingest and the Log Manager (summary, audit, security, errors, log
/// explorer, store and retention settings). Every query needs a time range (<c>from</c>/<c>to</c>, UTC) of
/// at most 31 days.
/// </summary>
/// <remarks>
/// Server-side code writes telemetry through <c>ILogger</c> and audit entries through <c>IAuditWriter</c>;
/// these actions are the HTTP surface. Guide: <c>doc/engine/engine-log.md</c>.
/// </remarks>
public interface ILogServices : IServices
{
   /// <summary>Claim name that opens the Log Manager, written without its module name.</summary>
   const string ManagerClaim = "Log Manager Access";

   /// <summary>Claim name that allows reading telemetry (log, errors, summary).</summary>
   const string TelemetryReadClaim = "Log Telemetry Read";

   /// <summary>Claim name that allows reading audit entries.</summary>
   const string AuditReadClaim = "Log Audit Read";

   /// <summary>Claim name that allows reading security audit (<c>System.Auth</c>, <c>System.Password</c>).</summary>
   const string SecurityReadClaim = "Log Security Read";

   /// <summary>Claim name that allows changing the log store and retention.</summary>
   const string SettingsClaim = "Log Manager Settings";

   /// <summary>
   /// Receives a batch of client telemetry from any signed-in user or robot. A batch id that was already
   /// received is ignored, so a client may resend after a lost answer. The server fills the sender, address
   /// and receive time itself.
   /// </summary>
   Task PostMeta_LogIngest(LogIngestBatch batch);

   /// <summary>Counters of the summary tab; audit counters only with the matching read claims.</summary>
   Task<LogSummary> GetMeta_LogSummary(DateTime from, DateTime to);

   /// <summary>Telemetry events, newest first.</summary>
   /// <param name="from">Start of the range (UTC).</param>
   /// <param name="to">End of the range (UTC).</param>
   /// <param name="kind">Only this family.</param>
   /// <param name="minLevel">Only this level and above.</param>
   /// <param name="category">Category prefix.</param>
   /// <param name="text">Text contained in the message.</param>
   /// <param name="requestId">Only one request.</param>
   /// <param name="userId">Only one user.</param>
   /// <param name="cursor">Cursor of the previous page.</param>
   /// <param name="take">Page size, 1-500.</param>
   Task<LogEventPage> GetMeta_LogEvents(DateTime from, DateTime to, LogEventKind? kind, LogEventLevel? minLevel,
      string? category, string? text, string? requestId, string? userId, string? cursor, int take);

   /// <summary>Error groups seen in the range, most recently seen first.</summary>
   Task<LogErrorPage> GetMeta_LogErrors(DateTime from, DateTime to, LogErrorState? state, string? cursor, int take);

   /// <summary>One error group and its latest occurrences.</summary>
   Task<LogErrorDetail> GetMeta_LogErrorDetail(string fingerprint);

   /// <summary>Changes the triage state of an error group.</summary>
   Task PostMeta_LogErrorState(string fingerprint, LogErrorState state);

   /// <summary>
   /// Audit entries, newest first. Groups <c>System.Auth</c> and <c>System.Password</c> need
   /// <see cref="SecurityReadClaim"/>, every other group <see cref="AuditReadClaim"/>; without a group filter the
   /// groups the caller may not read are left out.
   /// </summary>
   Task<AuditActivityPage> GetMeta_LogAudit(DateTime from, DateTime to, string? group, string? kind, string? targetKey,
      string? docNo, string? actorUserId, string? sessionId, string? cursor, int take);

   /// <summary>Failed sign-ins grouped per account and address per five minutes.</summary>
   Task<LogSignInFailureGroup[]> GetMeta_LogSignInFailures(DateTime from, DateTime to);

   /// <summary>The audit catalog (groups and kinds with their labels).</summary>
   Task<AuditCatalogGroup[]> GetMeta_AuditCatalog();

   /// <summary>Saved and active log stores.</summary>
   Task<LogStoreSettingsView> GetMeta_LogStores();

   /// <summary>Opens a draft store with its runtime credential and reports its schema.</summary>
   Task<LogStoreTestResult> PostGetMeta_LogStoreTest(LogStoreDraft store);

   /// <summary>Creates or upgrades the store schema with an owner credential that is not stored.</summary>
   Task<LogPrepareResult> PostGetMeta_LogStorePrepare(LogStorePrepareRequest request);

   /// <summary>Saves the store list; it applies after the API restarts. Refused when a store is not ready.</summary>
   Task<LogStoreSettingsView> PostGetMeta_LogStoreSave(LogStoreSave request);

   /// <summary>Retention rules and the last run.</summary>
   Task<LogRetentionView> GetMeta_LogRetention();

   /// <summary>Counts the rows the given rules would delete now, without saving them.</summary>
   Task<LogRetentionPreview> PostGetMeta_LogRetentionPreview(LogRetentionRules rules);

   /// <summary>Saves the retention rules; they apply from the next run without a restart.</summary>
   Task<LogRetentionView> PostGetMeta_LogRetentionSave(LogRetentionRules rules);
}
