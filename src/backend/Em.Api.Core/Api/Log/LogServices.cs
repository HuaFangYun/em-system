using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.Log;

/// <summary>
/// Log engine actions: client telemetry ingest and the Log Manager. Each action names its claim; the ingest
/// action only needs a signed-in caller. Registered by <c>AddLogServer</c>.
/// </summary>
[Module(Defaults.LogModuleName)]
public sealed class LogServices : ServicesBase, ILogServices
{
   private const int MaxBatchEvents = 1000;

   private LogQuery Query => GetRequiredService<LogQuery>();
   private LogStoreRegistry Stores => GetRequiredService<LogStoreRegistry>();
   private LogStoreSettings Settings => GetRequiredService<LogStoreSettings>();
   private LogRetention Retention => GetRequiredService<LogRetention>();
   private AuditReader Reader => GetRequiredService<AuditReader>();
   private AuditCatalog Catalog => GetRequiredService<AuditCatalog>();
   private SystemAudit Audit => GetRequiredService<SystemAudit>();

   private T GetRequiredService<T>() where T : notnull => App.ServiceProvider.GetRequiredService<T>();

   /// <inheritdoc />
   [PostAction]
   public async Task PostMeta_LogIngest(LogIngestBatch batch) {
      var sender = Request.RequireUserId();
      if (string.IsNullOrWhiteSpace(batch.BatchId) || batch.BatchId.Length > 64) throw new ActionException("A batch id of 1-64 characters is required.", 400);
      if (batch.Events.Length > MaxBatchEvents) throw new ActionException($"A batch may hold at most {MaxBatchEvents} events.", 413);
      using var _ = LogGuard.Suppress();

      // The client's identity fields are its own record of who was signed in when the event happened; the server
      // only fills what the client cannot know or must not choose.
      var address = Request.CallerAddress;
      var events = batch.Events.Where(e => e is not null).ToList();
      foreach (var e in events) {
         e.Source = "client";
         e.ClientAddress = address;
         e.Seq = 0;
      }
      if (batch.DroppedEvents > 0) {
         events.Add(new LogEvent {
            Time = DateTime.UtcNow, Level = LogEventLevel.Warning, Category = "Em.Log.Client", Source = "client", ClientAddress = address,
            Message = $"The client dropped {batch.DroppedEvents} log events because its local log folder was full.",
            App = events.FirstOrDefault()?.App, Instance = events.FirstOrDefault()?.Instance, UserId = sender
         });
      }
      // The batch row is stored in the same transaction as its events: a resend after a failed write is not
      // mistaken for a duplicate, and a resend after a lost answer is.
      await ((LogIngest)GetRequiredService<ILogIngest>()).WriteAsync(events, LogSender.User, sender, new ta_LogIngestBatch {
         cLogIngestBatchId = batch.BatchId, cLogIngestBatchTime = DateTime.UtcNow, cLogIngestBatchSender = sender,
         cLogIngestBatchEvents = batch.Events.Length
      }, CancellationToken.None);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.TelemetryReadClaim)]
   public Task<LogSummary> GetMeta_LogSummary(DateTime from, DateTime to) {
      using var _ = LogGuard.Suppress();
      return Query.SummaryAsync(from, to, Has(ILogServices.AuditReadClaim), Has(ILogServices.SecurityReadClaim), AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.TelemetryReadClaim)]
   public Task<LogEventPage> GetMeta_LogEvents(DateTime from, DateTime to, LogEventKind? kind, LogEventLevel? minLevel, string? category,
      string? text, string? requestId, string? userId, string? cursor, int take) {
      using var _ = LogGuard.Suppress();
      return Query.EventsAsync(from, to, kind, minLevel, category, text, requestId, userId, cursor, take, AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.TelemetryReadClaim)]
   public Task<LogErrorPage> GetMeta_LogErrors(DateTime from, DateTime to, LogErrorState? state, string? cursor, int take) {
      using var _ = LogGuard.Suppress();
      return Query.ErrorsAsync(from, to, state, cursor, take, AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.TelemetryReadClaim)]
   public Task<LogErrorDetail> GetMeta_LogErrorDetail(string fingerprint) {
      using var _ = LogGuard.Suppress();
      return Query.ErrorDetailAsync(fingerprint, AbortToken);
   }

   /// <inheritdoc />
   [PostAction(claim: ILogServices.TelemetryReadClaim)]
   public Task PostMeta_LogErrorState(string fingerprint, LogErrorState state) => Query.SetErrorStateAsync(fingerprint, state, CancellationToken.None);

   /// <inheritdoc />
   [GetAction]
   public async Task<AuditActivityPage> GetMeta_LogAudit(DateTime from, DateTime to, string? group, string? kind, string? targetKey, string? docNo,
      string? actorUserId, string? sessionId, string? cursor, int take) {
      (from, to) = LogQuery.CheckRange(from, to);
      using var _ = LogGuard.Suppress();
      // Security groups need Security Read, every other group Audit Read; either claim alone opens its part.
      var security = Has(ILogServices.SecurityReadClaim);
      var auditRead = Has(ILogServices.AuditReadClaim);
      if (!security && !auditRead)
         throw new ActionException($"This action requires the claim '{Defaults.LogModuleName}:{ILogServices.AuditReadClaim}' or '{ILogServices.SecurityReadClaim}'.", 403);
      IReadOnlyCollection<string> groups;
      if (!string.IsNullOrWhiteSpace(group)) {
         var isSecurity = SystemAuditCatalog.SecurityGroups.Contains(group);
         if (isSecurity ? !security : !auditRead)
            throw new ActionException($"Reading '{group}' requires the claim '{Defaults.LogModuleName}:{(isSecurity ? ILogServices.SecurityReadClaim : ILogServices.AuditReadClaim)}'.", 403);
         groups = [group];
      }
      else if (security && auditRead) groups = [];
      else groups = security ? SystemAuditCatalog.SecurityGroups : [.. Catalog.Groups.Select(g => g.Name).Except(SystemAuditCatalog.SecurityGroups)];
      return await Reader.QueryAsync(new AuditQuery {
         Groups = groups, Kinds = string.IsNullOrWhiteSpace(kind) ? [] : [kind], TargetKey = Empty(targetKey), DocNo = Empty(docNo),
         ActorUserId = Empty(actorUserId), SessionId = Empty(sessionId), From = from, To = to, Cursor = cursor, Take = take
      }, AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.SecurityReadClaim)]
   public Task<LogSignInFailureGroup[]> GetMeta_LogSignInFailures(DateTime from, DateTime to) {
      using var _ = LogGuard.Suppress();
      return Query.SignInFailuresAsync(from, to, AbortToken);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.ManagerClaim)]
   public Task<AuditCatalogGroup[]> GetMeta_AuditCatalog() =>
      Task.FromResult(Catalog.Groups.OrderBy(g => g.IsSystem ? 0 : 1).ThenBy(g => g.Name, StringComparer.Ordinal).Select(g => g.ToDto()).ToArray());

   /// <inheritdoc />
   [GetAction(claim: ILogServices.SettingsClaim)]
   public async Task<LogStoreSettingsView> GetMeta_LogStores() {
      var saved = await Settings.ReadStoresAsync(AbortToken);
      return View(saved);
   }

   /// <inheritdoc />
   [PostAction(claim: ILogServices.SettingsClaim)]
   public async Task<LogStoreTestResult> PostGetMeta_LogStoreTest(LogStoreDraft store) {
      var result = new LogStoreTestResult { EngineSchemaVersion = LogStoreDbContext.SchemaVersion };
      try {
         var handle = await HandleOfAsync(store);
         await using (var db = handle.CreateContext()) {
            await db.Database.OpenConnectionAsync(AbortToken);
            result.Connected = true;
         }
         try {
            result.SchemaVersion = await LogStartupCheck.CheckStoreAsync(handle, AbortToken);
            result.Ready = true;
            result.Message = "The store is ready.";
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            result.Message = $"Connected, but the store is not ready: {ex.GetBaseException().Message} Use Prepare.";
         }
      }
      catch (ActionException) { throw; }
      catch (Exception ex) when (ex is not OperationCanceledException) {
         result.Message = $"Could not connect: {ex.GetBaseException().Message}";
      }
      return result;
   }

   /// <inheritdoc />
   [PostAction(claim: ILogServices.SettingsClaim)]
   public async Task<LogPrepareResult> PostGetMeta_LogStorePrepare(LogStorePrepareRequest request) {
      if (request.Store.Provider != LogStoreProvider.MicrosoftSqlServer && !ProvidersReady)
         throw new ActionException($"Provider '{request.Store.Provider}' is not supported yet.", 400);
      var runtimePassword = request.Store.Password ?? await StoredPasswordAsync(request.Store.Id);
      var result = await LogStorePrepare.RunAsync(request, runtimePassword, AbortToken);
      await Audit.WriteAsync(SystemAuditCatalog.Settings, request.Store.Id ?? request.Store.Name, "logstore.prepared",
         $"Prepared log store '{request.Store.Name}' ({(result.Success ? "succeeded" : "failed")})", "logstore",
         new { request.Store.Name, request.Store.Provider, request.Store.Server, request.Store.Database, request.CreateDatabase,
               request.GrantRuntimeUser, steps = result.Steps.Select(s => $"{s.Name}: {s.Status}").ToArray() });
      return result;
   }

   /// <inheritdoc />
   [PostAction(claim: ILogServices.SettingsClaim)]
   public async Task<LogStoreSettingsView> PostGetMeta_LogStoreSave(LogStoreSave request) {
      if (Stores.EnvironmentOverride) throw new ActionException("EM_LOG_DB_CONNECTION overrides the write store; the list cannot be saved here.", 409);
      if (request.Stores.Count(s => s.Role == LogStoreRole.Write) != 1) throw new ActionException("Exactly one store must have the Write role.", 400);
      if (request.Stores.Count(s => s.Kind == LogStoreKind.Internal) > 1) throw new ActionException("The internal database can be listed once.", 400);
      var current = await Settings.ReadStoresAsync(AbortToken);
      var key = await Settings.KeyAsync(true, AbortToken);
      var next = new LogStoreDocument();
      foreach (var draft in request.Stores) {
         if (string.IsNullOrWhiteSpace(draft.Name)) throw new ActionException("Every store needs a name.", 400);
         if (draft.Kind == LogStoreKind.Connection && draft.Provider != LogStoreProvider.MicrosoftSqlServer && !ProvidersReady)
            throw new ActionException($"Provider '{draft.Provider}' is not supported yet.", 400);
         var old = current.Stores.FirstOrDefault(s => s.Id == draft.Id);
         var entry = new LogStoreEntry {
            Id = draft.Kind == LogStoreKind.Internal ? LogStoreDocument.InternalStoreId : draft.Id ?? Ulid.NewUlid().ToString(),
            Name = draft.Name.Trim(), Kind = draft.Kind, Role = draft.Role, Provider = draft.Provider, Server = draft.Server?.Trim(),
            Port = draft.Port, Database = draft.Database?.Trim(), IntegratedSecurity = draft.IntegratedSecurity, User = draft.User?.Trim(),
            Encrypt = draft.Encrypt, TrustServerCertificate = draft.TrustServerCertificate,
            Password = draft.Password is { } pw ? (pw.Length == 0 ? null : LogSecrets.Encrypt(key, pw)) : old?.Password,
            OldestEventUtc = old?.OldestEventUtc, NewestEventUtc = old?.NewestEventUtc
         };
         // A store may only become the write store or a read store when its schema is ready.
         if (entry.Kind == LogStoreKind.Connection && entry.Role != LogStoreRole.Detached) {
            var handle = await HandleOfAsync(draft);
            try { await LogStartupCheck.CheckStoreAsync(handle, AbortToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) {
               throw new ActionException($"Log store '{entry.Name}' is not ready: {ex.GetBaseException().Message} Use Prepare first.", 409);
            }
         }
         next.Stores.Add(entry);
      }
      var saved = await Settings.SaveStoresAsync(next, request.Revision, AbortToken);
      await Audit.WriteAsync(SystemAuditCatalog.Settings, "logstores", "logstore.changed", "Changed the log store list (applies after restart)", "settings",
         new { stores = saved.Stores.Select(s => new { s.Name, s.Kind, s.Role, s.Provider, s.Server, s.Database }).ToArray() });
      return View(saved);
   }

   /// <inheritdoc />
   [GetAction(claim: ILogServices.SettingsClaim)]
   public Task<LogRetentionView> GetMeta_LogRetention() => Retention.ViewAsync(AbortToken);

   /// <inheritdoc />
   [PostAction(claim: ILogServices.SettingsClaim)]
   public Task<LogRetentionPreview> PostGetMeta_LogRetentionPreview(LogRetentionRules rules) {
      using var _ = LogGuard.Suppress();
      return Retention.PreviewAsync(rules, AbortToken);
   }

   /// <inheritdoc />
   [PostAction(claim: ILogServices.SettingsClaim)]
   public async Task<LogRetentionView> PostGetMeta_LogRetentionSave(LogRetentionRules rules) {
      var (old, saved) = await Retention.SaveAsync(rules, CancellationToken.None);
      var changes = Models.Diff(old, saved);
      await Audit.WriteAsync(SystemAuditCatalog.Settings, "retention", "retention.changed", $"Changed log retention ({changes.Length} value(s))",
         "settings", changes: changes);
      return await Retention.ViewAsync(AbortToken);
   }

   /// <summary>True once the MySQL and PostgreSQL store scripts ship with the engine (Phase 9).</summary>
   internal static bool ProvidersReady => true;

   private bool Has(string claim) =>
      Request.IsAdmin || Request.IsDebugRequest || Request.Claims.Any(c =>
         string.Equals(c.ModuleName, Defaults.LogModuleName, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Name, claim, StringComparison.OrdinalIgnoreCase));

   private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

   private LogStoreSettingsView View(LogStoreDocument saved) {
      var active = Stores.ActiveEntries.Select(e => e.ToInfo()).ToArray();
      return new LogStoreSettingsView {
         Stores = [.. saved.Stores.Select(s => s.ToInfo())], Active = active, Revision = saved.Revision,
         EnvironmentOverride = Stores.EnvironmentOverride, EngineSchemaVersion = LogStoreDbContext.SchemaVersion,
         RestartRequired = !SameStores(saved.Stores, Stores.ActiveEntries)
      };
   }

   private static bool SameStores(IReadOnlyList<LogStoreEntry> a, IReadOnlyList<LogStoreEntry> b) =>
      a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Role == p.Second.Role && p.First.Kind == p.Second.Kind &&
         p.First.Server == p.Second.Server && p.First.Port == p.Second.Port && p.First.Database == p.Second.Database &&
         p.First.User == p.Second.User && p.First.Provider == p.Second.Provider && p.First.IntegratedSecurity == p.Second.IntegratedSecurity &&
         p.First.Encrypt == p.Second.Encrypt && p.First.TrustServerCertificate == p.Second.TrustServerCertificate &&
         (p.First.Password ?? []).AsSpan().SequenceEqual(p.Second.Password ?? []));

   private async Task<string?> StoredPasswordAsync(string? id) {
      if (id is null) return null;
      var doc = await Settings.ReadStoresAsync(AbortToken);
      var entry = doc.Stores.FirstOrDefault(s => s.Id == id);
      if (entry?.Password is not { Length: > 0 } cipher) return null;
      return LogSecrets.Decrypt(await Settings.KeyAsync(false, AbortToken), cipher);
   }

   private async Task<LogStoreHandle> HandleOfAsync(LogStoreDraft draft) {
      if (draft.Kind == LogStoreKind.Internal) {
         var core = Stores.Core;
         return new LogStoreHandle(LogStoreDocument.InternalStoreId, draft.Name, LogStoreKind.Internal, draft.Role, core.Provider, core.ConnectionString);
      }
      var password = draft.Password ?? await StoredPasswordAsync(draft.Id);
      var cs = LogStoreSettings.BuildConnectionString(draft.Provider, draft.Server, draft.Port, draft.Database, draft.IntegratedSecurity, draft.User,
         password, draft.Encrypt, draft.TrustServerCertificate);
      return new LogStoreHandle(draft.Id ?? "draft", draft.Name, LogStoreKind.Connection, draft.Role, (Em.Api.Shared.DatabaseProvider)(int)draft.Provider, cs);
   }

   private static class Models
   {
      public static AuditFieldChange[] Diff(LogRetentionRules old, LogRetentionRules saved) {
         var list = AuditDiff.Of(old, saved, ignore: ["Revision"]).ToList();
         foreach (var group in saved.Audit) {
            var before = old.Audit.FirstOrDefault(a => a.Group == group.Group);
            if (before?.Months != group.Months)
               list.Add(new AuditFieldChange { Field = "Audit." + group.Group, Label = $"Audit {group.Group} (months)",
                  Old = before?.Months?.ToString() ?? "never", New = group.Months?.ToString() ?? "never" });
         }
         return [.. list];
      }
   }
}
