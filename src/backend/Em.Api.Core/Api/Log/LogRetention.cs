using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core.Log;

/// <summary>
/// Retention of the log stores: rules kept in <c>ta_Meta</c> (<c>Em.Log.Retention</c>, applied without a restart),
/// a preview that counts what would be deleted, and the daily run that deletes in small batches on every store.
/// Expired data is deleted; there is no archive.
/// </summary>
internal sealed class LogRetention(LogStoreRegistry stores, LogStoreSettings settings, AuditCatalog catalog, ILogger<LogRetention> logger)
{
   private const int BatchSize = 5000;
   private const int IngestBatchDays = 7;
   private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

   public async Task<LogRetentionView> ViewAsync(CancellationToken ct) {
      var rules = await ReadRulesAsync(ct);
      var last = await settings.ReadValueAsync(LogStoreSettings.RetentionLastRunKey, ct);
      var view = new LogRetentionView { Rules = rules };
      if (last is not null) {
         try {
            var run = JsonSerializer.Deserialize<LastRun>(last, Json);
            view.LastRunUtc = run?.Utc;
            view.LastRunSummary = run?.Summary;
         }
         catch (JsonException) { }
      }
      return view;
   }

   public async Task<LogRetentionRules> ReadRulesAsync(CancellationToken ct) {
      var raw = await settings.ReadValueAsync(LogStoreSettings.RetentionKey, ct);
      var rules = raw is null ? new LogRetentionRules() : JsonSerializer.Deserialize<LogRetentionRules>(raw, Json) ?? new LogRetentionRules();
      return Complete(rules);
   }

   /// <summary>Adds missing groups with their defaults and drops groups no longer registered.</summary>
   internal LogRetentionRules Complete(LogRetentionRules rules) {
      var byGroup = rules.Audit.Where(a => !string.IsNullOrWhiteSpace(a.Group)).GroupBy(a => a.Group).ToDictionary(g => g.Key, g => g.First());
      rules.Audit = [.. catalog.Groups.OrderBy(g => g.IsSystem ? 0 : 1).ThenBy(g => g.Name, StringComparer.Ordinal).Select(g => new LogAuditRetention {
         Group = g.Name, IsSystem = g.IsSystem, MinMonths = g.MinRetentionMonths,
         Months = byGroup.TryGetValue(g.Name, out var saved) ? saved.Months : g.IsSystem ? AuditCatalog.SystemDefaultMonths : null
      })];
      return rules;
   }

   /// <summary>Checks lower bounds; throws 400 naming the first wrong value.</summary>
   internal LogRetentionRules Validate(LogRetentionRules rules) {
      rules = Complete(rules);
      if (rules.InformationDays < 1 || rules.WarningDays < 1 || rules.ErrorDays < 1 || rules.AccessDays < 1)
         throw new ActionException("Telemetry and access retention must be at least 1 day.", 400);
      if (rules.ErrorGroupDays < 30) throw new ActionException("Error groups must be kept at least 30 days after they were last seen.", 400);
      if (rules.TelemetryMaxGb is <= 0) throw new ActionException("The telemetry size limit must be greater than 0 GB, or empty for no limit.", 400);
      if (rules.RunHour is < 0 or > 23) throw new ActionException("The run hour must be 0-23.", 400);
      foreach (var a in rules.Audit) {
         if (a.IsSystem && a.Months is null) throw new ActionException($"System audit '{a.Group}' cannot be kept forever; set months (at least {AuditCatalog.SystemMinMonths}).", 400);
         if (a.Months is { } m && a.MinMonths is { } min && m < min)
            throw new ActionException($"Audit '{a.Group}' must be kept at least {min} months.", 400);
         if (a.Months is < 1) throw new ActionException($"Audit '{a.Group}' must be kept at least 1 month.", 400);
      }
      return rules;
   }

   public async Task<(LogRetentionRules Old, LogRetentionRules New)> SaveAsync(LogRetentionRules rules, CancellationToken ct) {
      rules = Validate(rules);
      var current = await ReadRulesAsync(ct);
      if (current.Revision != rules.Revision) throw new ActionException("The retention rules were changed by someone else. Reload and try again.", 409);
      rules.Revision = current.Revision + 1;
      await settings.WriteValueAsync(LogStoreSettings.RetentionKey, JsonSerializer.Serialize(rules), "Log retention rules", ct);
      return (current, rules);
   }

   public async Task<LogRetentionPreview> PreviewAsync(LogRetentionRules rules, CancellationToken ct) {
      rules = Validate(rules);
      var totals = new Dictionary<string, long>();
      var warnings = new List<string>();
      foreach (var store in stores.All) {
         try {
            await using var db = store.CreateContext();
            foreach (var scope in Scopes(rules, DateTime.UtcNow)) {
               var n = scope.Events is { } ev ? await db.Events.LongCountAsync(ev, ct)
                  : scope.Audit is { } au ? await db.Audit.LongCountAsync(au, ct)
                  : scope.Errors is { } er ? await db.Errors.LongCountAsync(er, ct)
                  : await db.Batches.LongCountAsync(scope.Batches!, ct);
               totals[scope.Name] = totals.GetValueOrDefault(scope.Name) + n;
            }
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            warnings.Add($"Log store '{store.Name}' could not be counted: {ex.GetBaseException().Message}");
         }
      }
      return new LogRetentionPreview {
         Items = [.. totals.Select(t => new LogRetentionPreviewItem { Scope = t.Key, Rows = t.Value })], Warnings = [.. warnings]
      };
   }

   /// <summary>Deletes expired data on every store; returns a one-line summary.</summary>
   public async Task<string> RunAsync(CancellationToken ct) {
      using var _ = LogGuard.Suppress();
      var started = DateTime.UtcNow;
      var rules = await ReadRulesAsync(ct);
      var deleted = new Dictionary<string, long>();
      var errors = new List<string>();
      foreach (var store in stores.All) {
         try {
            await using var db = store.CreateContext();
            foreach (var scope in Scopes(rules, started)) {
               var n = scope.Events is { } ev ? await DeleteEventsAsync(db, ev, ct)
                  : scope.Audit is { } au ? await DeleteAuditAsync(db, au, ct)
                  : scope.Errors is { } er ? await db.Errors.Where(er).ExecuteDeleteAsync(ct)
                  : await db.Batches.Where(scope.Batches!).ExecuteDeleteAsync(ct);
               if (n > 0) deleted[scope.Name] = deleted.GetValueOrDefault(scope.Name) + n;
            }
            if (rules.TelemetryMaxGb is { } gb) {
               var n = await TrimToSizeAsync(db, (long)(gb * 1024 * 1024 * 1024), ct);
               if (n > 0) deleted["Telemetry size limit"] = deleted.GetValueOrDefault("Telemetry size limit") + n;
            }
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            errors.Add($"{store.Name}: {ex.GetBaseException().Message}");
            logger.LogWarning("Log retention failed on store '{Store}': {Reason}", store.Name, ex.GetBaseException().Message);
         }
      }
      await UpdateStoreRangesAsync(ct);
      var summary = deleted.Count == 0 ? "Nothing expired." : string.Join(", ", deleted.Select(d => $"{d.Key}: {d.Value}"));
      if (errors.Count > 0) summary += " Errors: " + string.Join("; ", errors);
      summary += $" ({(DateTime.UtcNow - started).TotalSeconds:0.0} s)";
      await settings.WriteValueAsync(LogStoreSettings.RetentionLastRunKey,
         JsonSerializer.Serialize(new LastRun { Utc = started, Summary = summary }), "Last log retention run", ct);
      return summary;
   }

   private sealed class LastRun
   {
      public DateTime Utc { get; set; }
      public string Summary { get; set; } = "";
   }

   internal sealed record Scope(string Name, Expression<Func<ta_LogEvent, bool>>? Events = null, Expression<Func<ta_LogAudit, bool>>? Audit = null,
      Expression<Func<ta_LogError, bool>>? Errors = null, Expression<Func<ta_LogIngestBatch, bool>>? Batches = null);

   internal static IEnumerable<Scope> Scopes(LogRetentionRules rules, DateTime now) {
      var info = now.AddDays(-rules.InformationDays);
      var warn = now.AddDays(-rules.WarningDays);
      var error = now.AddDays(-rules.ErrorDays);
      var access = now.AddDays(-rules.AccessDays);
      var groups = now.AddDays(-rules.ErrorGroupDays);
      var batches = now.AddDays(-IngestBatchDays);
      const int accessKind = (int)LogEventKind.Access;
      yield return new Scope("Telemetry Information", Events: e => e.cLogEventKind != accessKind && e.cLogEventLevel <= (int)LogEventLevel.Information && e.cLogEventTime < info);
      yield return new Scope("Telemetry Warning", Events: e => e.cLogEventKind != accessKind && e.cLogEventLevel == (int)LogEventLevel.Warning && e.cLogEventTime < warn);
      yield return new Scope("Telemetry Error", Events: e => e.cLogEventKind != accessKind && e.cLogEventLevel >= (int)LogEventLevel.Error && e.cLogEventTime < error);
      yield return new Scope("Access", Events: e => e.cLogEventKind == accessKind && e.cLogEventTime < access);
      yield return new Scope("Error groups", Errors: e => e.cLogErrorLastSeen < groups);
      yield return new Scope("Ingest batches", Batches: b => b.cLogIngestBatchTime < batches);
      foreach (var a in rules.Audit.Where(a => a.Months is not null)) {
         var group = a.Group;
         var cut = now.AddMonths(-a.Months!.Value);
         yield return new Scope($"Audit {group}", Audit: r => r.cLogAuditGroup == group && r.cLogAuditTime < cut);
      }
   }

   // Deletes in batches of BatchSize by sequence so no single statement locks a large part of the table.
   private static async Task<long> DeleteEventsAsync(LogStoreDbContext db, Expression<Func<ta_LogEvent, bool>> condition, CancellationToken ct) {
      long total = 0;
      while (true) {
         var max = await db.Events.Where(condition).OrderBy(e => e.cLogEventSeq).Skip(BatchSize - 1).Select(e => (long?)e.cLogEventSeq).FirstOrDefaultAsync(ct);
         if (max is null) return total + await db.Events.Where(condition).ExecuteDeleteAsync(ct);
         total += await db.Events.Where(condition).Where(e => e.cLogEventSeq <= max).ExecuteDeleteAsync(ct);
         await Task.Delay(200, ct);
      }
   }

   private static async Task<long> DeleteAuditAsync(LogStoreDbContext db, Expression<Func<ta_LogAudit, bool>> condition, CancellationToken ct) {
      long total = 0;
      while (true) {
         var max = await db.Audit.Where(condition).OrderBy(e => e.cLogAuditSeq).Skip(BatchSize - 1).Select(e => (long?)e.cLogAuditSeq).FirstOrDefaultAsync(ct);
         if (max is null) return total + await db.Audit.Where(condition).ExecuteDeleteAsync(ct);
         total += await db.Audit.Where(condition).Where(e => e.cLogAuditSeq <= max).ExecuteDeleteAsync(ct);
         await Task.Delay(200, ct);
      }
   }

   /// <summary>
   /// Estimated size = rows × average row size of the newest 1000 rows (text columns counted as two bytes per
   /// character plus a fixed overhead). Deletes the oldest events until the estimate is under the limit.
   /// </summary>
   private static async Task<long> TrimToSizeAsync(LogStoreDbContext db, long limitBytes, CancellationToken ct) {
      var rows = await db.Events.LongCountAsync(ct);
      if (rows == 0) return 0;
      var sample = await db.Events.OrderByDescending(e => e.cLogEventSeq).Take(1000)
         .Select(e => (e.cLogEventMessage.Length + (e.cLogEventProperties ?? "").Length + (e.cLogEventException ?? "").Length +
                       (e.cLogEventTemplate ?? "").Length + e.cLogEventCategory.Length) * 2 + 400).ToListAsync(ct);
      var average = sample.Count == 0 ? 600 : sample.Average();
      var excess = (long)((rows * average - limitBytes) / average);
      if (excess <= 0) return 0;
      var cutSeq = await db.Events.OrderBy(e => e.cLogEventSeq).Skip((int)Math.Min(excess - 1, int.MaxValue)).Select(e => (long?)e.cLogEventSeq).FirstOrDefaultAsync(ct);
      if (cutSeq is null) return 0;
      return await DeleteEventsAsync(db, e => e.cLogEventSeq <= cutSeq, ct);
   }

   // Records the oldest and newest event time of each saved store, shown in Settings > Database.
   private async Task UpdateStoreRangesAsync(CancellationToken ct) {
      try {
         var doc = await settings.ReadStoresAsync(ct);
         var changed = false;
         foreach (var entry in doc.Stores) {
            var handle = stores.All.FirstOrDefault(s => s.Id == entry.Id);
            if (handle is null) continue;
            await using var db = handle.CreateContext();
            var oldest = await db.Events.OrderBy(e => e.cLogEventSeq).Select(e => (DateTime?)e.cLogEventTime).FirstOrDefaultAsync(ct);
            var newest = await db.Events.OrderByDescending(e => e.cLogEventSeq).Select(e => (DateTime?)e.cLogEventTime).FirstOrDefaultAsync(ct);
            if (oldest != entry.OldestEventUtc || newest != entry.NewestEventUtc) {
               entry.OldestEventUtc = oldest;
               entry.NewestEventUtc = newest;
               changed = true;
            }
         }
         if (changed) await settings.SaveStoresAsync(doc, doc.Revision, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException) {
         logger.LogDebug("Log store ranges were not updated: {Reason}", ex.Message);
      }
   }
}

/// <summary>Runs <see cref="LogRetention"/> once a day at the configured hour (server local time).</summary>
internal sealed class LogRetentionBackground(LogRetention retention, IServiceProvider services, ILogger<LogRetentionBackground> logger) : BackgroundService
{
   private DateOnly _lastRunDay;

   protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      while (!stoppingToken.IsCancellationRequested) {
         try {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            var rules = await retention.ReadRulesAsync(stoppingToken);
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (now.Hour != rules.RunHour || _lastRunDay == today) continue;
            _lastRunDay = today;
            var summary = await retention.RunAsync(stoppingToken);
            logger.LogInformation("Log retention ran: {Summary}", summary);
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SystemAudit>().WriteAsync(SystemAuditCatalog.Settings, "retention", "retention.run",
               summary, "settings", ct: stoppingToken);
         }
         catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
         catch (Exception ex) {
            logger.LogWarning("Log retention job failed: {Reason}", ex.Message);
         }
      }
   }
}
