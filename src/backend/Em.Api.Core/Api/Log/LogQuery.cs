using System.Text;
using System.Text.Json;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log;

/// <summary>
/// Log Manager queries over every store. Only operations available on SQL Server, MySQL and PostgreSQL are used
/// (equality, ranges, <c>LIKE</c>, ordering, paging); a time range is always required.
/// </summary>
internal sealed class LogQuery(LogStoreRegistry stores, LogRuntime runtime, AuditReader audit)
{
   /// <summary>Longest range of one query.</summary>
   public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

   public static (DateTime From, DateTime To) CheckRange(DateTime from, DateTime to) {
      from = AsUtc(from);
      to = AsUtc(to);
      if (to <= from) throw new ActionException("'to' must be after 'from'.", 400);
      if (to - from > MaxRange + TimeSpan.FromMinutes(1)) throw new ActionException("The time range may be at most 31 days.", 400);
      return (from, to);
   }

   private static DateTime AsUtc(DateTime value) => value.Kind switch {
      DateTimeKind.Utc => value,
      DateTimeKind.Local => value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
   };

   public async Task<LogEventPage> EventsAsync(DateTime from, DateTime to, LogEventKind? kind, LogEventLevel? minLevel, string? category,
      string? text, string? requestId, string? userId, string? cursor, int take, CancellationToken ct) {
      (from, to) = CheckRange(from, to);
      take = Math.Clamp(take, 1, 500);
      var position = DecodeEventCursor(cursor);
      var rows = new List<(int Store, ta_LogEvent Row)>();
      var warnings = new List<string>();
      var index = 0;
      foreach (var store in stores.All) {
         var storeIndex = index++;
         try {
            await using var db = store.CreateContext();
            var q = db.Events.Where(e => e.cLogEventTime >= from && e.cLogEventTime < to);
            if (kind is { } k) q = q.Where(e => e.cLogEventKind == (int)k);
            if (minLevel is { } level) q = q.Where(e => e.cLogEventLevel >= (int)level);
            if (!string.IsNullOrWhiteSpace(category)) { var c = Like(category) + "%"; q = q.Where(e => EF.Functions.Like(e.cLogEventCategory, c)); }
            if (!string.IsNullOrWhiteSpace(text)) { var t = "%" + Like(text) + "%"; q = q.Where(e => EF.Functions.Like(e.cLogEventMessage, t)); }
            if (!string.IsNullOrWhiteSpace(requestId)) q = q.Where(e => e.cLogEventRequestId == requestId);
            if (!string.IsNullOrWhiteSpace(userId)) q = q.Where(e => e.cLogEventUserId == userId);
            if (position is { } p) {
               var (time, pStore, seq) = p;
               // Order: time desc, store index asc, seq desc - total across stores.
               if (storeIndex < pStore) q = q.Where(e => e.cLogEventTime < time);
               else if (storeIndex == pStore) q = q.Where(e => e.cLogEventTime < time || (e.cLogEventTime == time && e.cLogEventSeq < seq));
               else q = q.Where(e => e.cLogEventTime <= time);
            }
            var list = await q.OrderByDescending(e => e.cLogEventTime).ThenByDescending(e => e.cLogEventSeq).Take(take + 1).ToListAsync(ct);
            rows.AddRange(list.Select(r => (storeIndex, r)));
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            warnings.Add($"Log store '{store.Name}' is not available: {ex.GetBaseException().Message}");
         }
      }
      var merged = rows.OrderByDescending(r => r.Row.cLogEventTime).ThenBy(r => r.Store).ThenByDescending(r => r.Row.cLogEventSeq).ToList();
      var page = merged.Take(take).ToList();
      return new LogEventPage {
         Items = [.. page.Select(r => LogIngest.FromRow(r.Row))],
         NextCursor = merged.Count > take ? EncodeEventCursor(page[^1].Row.cLogEventTime, page[^1].Store, page[^1].Row.cLogEventSeq) : null,
         Warnings = [.. warnings]
      };
   }

   public async Task<LogErrorPage> ErrorsAsync(DateTime from, DateTime to, LogErrorState? state, string? cursor, int take, CancellationToken ct) {
      (from, to) = CheckRange(from, to);
      take = Math.Clamp(take, 1, 500);
      var position = DecodeErrorCursor(cursor);
      await using var db = stores.Write.CreateContext();
      var q = db.Errors.Where(e => e.cLogErrorLastSeen >= from && e.cLogErrorFirstSeen < to);
      if (state is { } s) q = q.Where(e => e.cLogErrorState == (int)s);
      if (position is { } p) {
         var (time, id) = p;
         q = q.Where(e => e.cLogErrorLastSeen < time || (e.cLogErrorLastSeen == time && string.Compare(e.cLogErrorId, id) < 0));
      }
      var list = await q.OrderByDescending(e => e.cLogErrorLastSeen).ThenByDescending(e => e.cLogErrorId).Take(take + 1).ToListAsync(ct);
      var page = list.Take(take).ToList();
      return new LogErrorPage {
         Items = [.. page.Select(ToGroup)],
         NextCursor = list.Count > take ? Encode($"{page[^1].cLogErrorLastSeen.Ticks}|{page[^1].cLogErrorId}") : null
      };
   }

   public async Task<LogErrorDetail> ErrorDetailAsync(string fingerprint, CancellationToken ct) {
      await using var db = stores.Write.CreateContext();
      var group = await db.Errors.SingleOrDefaultAsync(e => e.cLogErrorFingerprint == fingerprint, ct)
         ?? throw new ActionException($"Error group '{fingerprint}' was not found.", 404);
      var recent = new List<ta_LogEvent>();
      foreach (var store in stores.All) {
         try {
            await using var s = store.CreateContext();
            recent.AddRange(await s.Events.Where(e => e.cLogEventFingerprint == fingerprint)
               .OrderByDescending(e => e.cLogEventTime).Take(20).ToListAsync(ct));
         }
         catch (Exception ex) when (ex is not OperationCanceledException) { }
      }
      return new LogErrorDetail {
         Group = ToGroup(group),
         Recent = [.. recent.OrderByDescending(e => e.cLogEventTime).Take(20).Select(LogIngest.FromRow)]
      };
   }

   public async Task SetErrorStateAsync(string fingerprint, LogErrorState state, CancellationToken ct) {
      if (!Enum.IsDefined(state)) throw new ActionException("Unknown error state.", 400);
      await using var db = stores.Write.CreateContext();
      var row = await db.Errors.AsTracking().SingleOrDefaultAsync(e => e.cLogErrorFingerprint == fingerprint, ct)
         ?? throw new ActionException($"Error group '{fingerprint}' was not found.", 404);
      row.cLogErrorState = (int)state;
      row.ustamp = DateTime.UtcNow;
      await db.SaveChangesAsync(ct);
   }

   public async Task<LogSummary> SummaryAsync(DateTime from, DateTime to, bool withAudit, bool withSecurity, CancellationToken ct) {
      (from, to) = CheckRange(from, to);
      var summary = new LogSummary {
         DroppedEvents = runtime.Dropped, PendingSegments = runtime.PendingSegments, StoreName = stores.Write.Name,
         StoreProvider = stores.Write.Provider.ToString(), StoreHealthy = runtime.StoreHealthy, StoreMessage = runtime.StoreError,
         AuditOutboxPending = Audit.AuditShipper.Pending
      };
      foreach (var store in stores.All) {
         try {
            await using var db = store.CreateContext();
            var range = db.Events.Where(e => e.cLogEventTime >= from && e.cLogEventTime < to);
            summary.Errors += await range.LongCountAsync(e => e.cLogEventLevel >= (int)LogEventLevel.Error, ct);
            summary.Warnings += await range.LongCountAsync(e => e.cLogEventLevel == (int)LogEventLevel.Warning, ct);
            summary.Requests += await range.LongCountAsync(e => e.cLogEventKind == (int)LogEventKind.Access, ct);
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            summary.StoreMessage ??= $"Log store '{store.Name}' is not available: {ex.GetBaseException().Message}";
         }
      }
      try {
         await using var db = stores.Write.CreateContext();
         summary.NewErrorGroups = await db.Errors.LongCountAsync(e => e.cLogErrorFirstSeen >= from && e.cLogErrorFirstSeen < to, ct);
         summary.OpenErrorGroups = await db.Errors.LongCountAsync(e => e.cLogErrorState == (int)LogErrorState.New, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException) { }
      if (withAudit) summary.AuditEntries = await audit.CountAsync(new AuditQuery { From = from, To = to }, ct);
      if (withSecurity)
         summary.FailedSignIns = await audit.CountAsync(new AuditQuery { Groups = [SystemAuditCatalog.Auth], Kinds = ["signin.failed"], From = from, To = to }, ct);
      return summary;
   }

   public async Task<LogSignInFailureGroup[]> SignInFailuresAsync(DateTime from, DateTime to, CancellationToken ct) {
      (from, to) = CheckRange(from, to);
      var rows = new List<AuditActivity>();
      string? cursor = null;
      do {
         var page = await audit.QueryAsync(new AuditQuery {
            Groups = [SystemAuditCatalog.Auth], Kinds = ["signin.failed"], From = from, To = to, Cursor = cursor, Take = 500
         }, ct);
         rows.AddRange(page.Items);
         cursor = page.NextCursor;
      } while (cursor is not null && rows.Count < 20_000);

      return [.. rows.GroupBy(r => (Window: new DateTime(r.Time.Ticks - r.Time.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc),
            Account: AccountTried(r), r.ClientAddress))
         .Select(g => new LogSignInFailureGroup { WindowStart = g.Key.Window, Account = g.Key.Account, Address = g.Key.ClientAddress, Count = g.Count() })
         .OrderByDescending(g => g.WindowStart).ThenByDescending(g => g.Count)];
   }

   private static string? AccountTried(AuditActivity a) {
      if (a.Detail is null) return a.TargetKey;
      try {
         using var doc = JsonDocument.Parse(a.Detail);
         return doc.RootElement.TryGetProperty("accountTried", out var v) ? v.GetString() : a.TargetKey;
      }
      catch (JsonException) { return a.TargetKey; }
   }

   internal static LogErrorGroup ToGroup(ta_LogError r) => new() {
      Fingerprint = r.cLogErrorFingerprint, ExceptionType = r.cLogErrorType, TopFrame = r.cLogErrorTopFrame,
      SampleMessage = r.cLogErrorMessage, Count = r.cLogErrorCount, FirstSeen = DateTime.SpecifyKind(r.cLogErrorFirstSeen, DateTimeKind.Utc),
      LastSeen = DateTime.SpecifyKind(r.cLogErrorLastSeen, DateTimeKind.Utc), LastApp = r.cLogErrorLastApp,
      LastAppVersion = r.cLogErrorLastAppVersion, AffectedUsers = r.cLogErrorUserCount, State = (LogErrorState)r.cLogErrorState
   };

   /// <summary>Escapes the <c>LIKE</c> wildcards of user text (the same rule on every provider: bracket-free).</summary>
   internal static string Like(string text) => text.Replace("%", "").Replace("_", "").Replace("[", "").Trim();

   private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

   private static string Decode(string cursor) {
      try { return Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
      catch (FormatException) { throw new ActionException("The cursor is not valid.", 400); }
   }

   internal static string EncodeEventCursor(DateTime time, int store, long seq) => Encode($"{time.Ticks}|{store}|{seq}");

   internal static (DateTime Time, int Store, long Seq)? DecodeEventCursor(string? cursor) {
      if (string.IsNullOrWhiteSpace(cursor)) return null;
      var parts = Decode(cursor).Split('|');
      if (parts.Length != 3 || !long.TryParse(parts[0], out var ticks) || !int.TryParse(parts[1], out var store) || !long.TryParse(parts[2], out var seq))
         throw new ActionException("The cursor is not valid.", 400);
      return (new DateTime(ticks, DateTimeKind.Utc), store, seq);
   }

   private static (DateTime Time, string Id)? DecodeErrorCursor(string? cursor) {
      if (string.IsNullOrWhiteSpace(cursor)) return null;
      var text = Decode(cursor);
      var bar = text.IndexOf('|');
      if (bar <= 0 || !long.TryParse(text[..bar], out var ticks)) throw new ActionException("The cursor is not valid.", 400);
      return (new DateTime(ticks, DateTimeKind.Utc), text[(bar + 1)..]);
   }
}
