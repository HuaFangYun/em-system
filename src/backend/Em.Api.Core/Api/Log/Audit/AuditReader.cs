using System.Text;
using System.Text.Json;
using Em.Api.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log.Audit;

/// <summary>Filter of an audit read. Every condition is optional; a page is ordered newest first.</summary>
public sealed class AuditQuery
{
   /// <summary>Only these groups; empty = every group.</summary>
   public IReadOnlyCollection<string> Groups { get; init; } = [];

   /// <summary>Only these kinds; empty = every kind.</summary>
   public IReadOnlyCollection<string> Kinds { get; init; } = [];

   /// <summary>Only this target type.</summary>
   public string? TargetType { get; init; }

   /// <summary>Only this target key.</summary>
   public string? TargetKey { get; init; }

   /// <summary>Only this document number.</summary>
   public string? DocNo { get; init; }

   /// <summary>Only this actor.</summary>
   public string? ActorUserId { get; init; }

   /// <summary>Only this session.</summary>
   public string? SessionId { get; init; }

   /// <summary>From (UTC, inclusive).</summary>
   public DateTime? From { get; init; }

   /// <summary>To (UTC, exclusive).</summary>
   public DateTime? To { get; init; }

   /// <summary>Opaque cursor of the previous page.</summary>
   public string? Cursor { get; init; }

   /// <summary>Page size, 1-500.</summary>
   public int Take { get; init; } = 50;
}

/// <summary>
/// Reads audit entries for server-side module code (no HTTP). Merges the write store, the read stores and the
/// outbox rows not moved yet, so an entry is visible right after it was written. It does not check read rights;
/// HTTP actions check <c>CanRead</c> before calling it.
/// </summary>
public interface IAuditReader
{
   /// <summary>History of one document by key.</summary>
   Task<AuditActivityPage> ByDocAsync(string group, string key, string? cursor, int take, CancellationToken ct = default);

   /// <summary>History of one document by document number.</summary>
   Task<AuditActivityPage> ByDocNoAsync(string group, string docNo, string? cursor, int take, CancellationToken ct = default);

   /// <summary>Activity of a group.</summary>
   Task<AuditActivityPage> ByTypeAsync(string group, DateTime? from, DateTime? to, string? userId, IReadOnlyCollection<string>? kinds,
      string? cursor, int take, CancellationToken ct = default);

   /// <summary>Entries newer than (<paramref name="afterTime"/>, <paramref name="afterId"/>), oldest first, at most 500.</summary>
   Task<AuditActivity[]> SinceAsync(string group, string? key, DateTime afterTime, string? afterId, CancellationToken ct = default);

   /// <summary>Any combination of filters.</summary>
   Task<AuditActivityPage> QueryAsync(AuditQuery query, CancellationToken ct = default);
}

/// <summary>Implementation of <see cref="IAuditReader"/>.</summary>
internal sealed class AuditReader(LogStoreRegistry stores, AuditCatalog catalog) : IAuditReader
{
   private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

   public Task<AuditActivityPage> ByDocAsync(string group, string key, string? cursor, int take, CancellationToken ct = default) =>
      QueryAsync(new AuditQuery { Groups = [group], TargetKey = key, Cursor = cursor, Take = take }, ct);

   public async Task<AuditActivityPage> ByDocNoAsync(string group, string docNo, string? cursor, int take, CancellationToken ct = default) {
      var page = await QueryAsync(new AuditQuery { Groups = [group], DocNo = docNo, Cursor = cursor, Take = take }, ct);
      if (page.Items.Select(i => i.TargetKey).Distinct().Count() > 1)
         throw new Em.Shared.ActionException($"Document number '{docNo}' is used by more than one {group}; open it by key instead.", 409);
      return page;
   }

   public Task<AuditActivityPage> ByTypeAsync(string group, DateTime? from, DateTime? to, string? userId, IReadOnlyCollection<string>? kinds,
      string? cursor, int take, CancellationToken ct = default) =>
      QueryAsync(new AuditQuery { Groups = [group], From = from, To = to, ActorUserId = userId, Kinds = kinds ?? [], Cursor = cursor, Take = take }, ct);

   public async Task<AuditActivity[]> SinceAsync(string group, string? key, DateTime afterTime, string? afterId, CancellationToken ct = default) {
      var id = afterId ?? "";
      var result = new List<ta_LogAudit>();
      foreach (var source in Sources()) {
         try {
            await using var db = source.Create();
            var after = Utc(afterTime);
            var q = Filter(source.Query(db), new AuditQuery { Groups = [group], TargetKey = key })
               .Where(r => r.cLogAuditTime > after || (r.cLogAuditTime == after && string.Compare(r.cLogAuditId, id) > 0))
               .OrderBy(r => r.cLogAuditTime).ThenBy(r => r.cLogAuditId).Take(500);
            result.AddRange(await q.ToListAsync(ct));
         }
         catch (Exception ex) when (ex is not OperationCanceledException) { }
      }
      return [.. result.DistinctBy(r => r.cLogAuditId).OrderBy(r => r.cLogAuditTime).ThenBy(r => r.cLogAuditId, StringComparer.Ordinal)
         .Take(500).Select(ToActivity)];
   }

   public async Task<AuditActivityPage> QueryAsync(AuditQuery query, CancellationToken ct = default) {
      var take = Math.Clamp(query.Take, 1, 500);
      var position = DecodeCursor(query.Cursor);
      var rows = new List<ta_LogAudit>();
      var warnings = new List<string>();
      foreach (var source in Sources()) {
         try {
            await using var db = source.Create();
            var q = Filter(source.Query(db), query);
            if (position is { } p) {
               var (time, id) = p;
               q = q.Where(r => r.cLogAuditTime < time || (r.cLogAuditTime == time && string.Compare(r.cLogAuditId, id) < 0));
            }
            rows.AddRange(await q.OrderByDescending(r => r.cLogAuditTime).ThenByDescending(r => r.cLogAuditId).Take(take + 1).ToListAsync(ct));
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            warnings.Add($"{source.Name} is not available: {ex.GetBaseException().Message}");
         }
      }

      var merged = rows.DistinctBy(r => r.cLogAuditId)
         .OrderByDescending(r => r.cLogAuditTime).ThenByDescending(r => r.cLogAuditId, StringComparer.Ordinal).ToList();
      var page = merged.Take(take).ToList();
      return new AuditActivityPage {
         Items = [.. page.Select(ToActivity)],
         NextCursor = merged.Count > take ? EncodeCursor(page[^1].cLogAuditTime, page[^1].cLogAuditId) : null,
         Warnings = [.. warnings]
      };
   }

   /// <summary>Counts entries per scope for the summary and retention preview.</summary>
   internal async Task<long> CountAsync(AuditQuery query, CancellationToken ct) {
      long total = 0;
      foreach (var source in Sources()) {
         try {
            await using var db = source.Create();
            total += await Filter(source.Query(db), query).LongCountAsync(ct);
         }
         catch (Exception ex) when (ex is not OperationCanceledException) { }
      }
      return total;
   }

   internal static IQueryable<ta_LogAudit> Filter(IQueryable<ta_LogAudit> q, AuditQuery f) {
      if (f.Groups.Count == 1) { var g = f.Groups.First(); q = q.Where(r => r.cLogAuditGroup == g); }
      else if (f.Groups.Count > 1) { var groups = f.Groups.ToArray(); q = q.Where(r => groups.Contains(r.cLogAuditGroup)); }
      if (f.Kinds.Count > 0) { var kinds = f.Kinds.ToArray(); q = q.Where(r => kinds.Contains(r.cLogAuditKind)); }
      if (f.TargetType is { } tt) q = q.Where(r => r.cLogAuditTargetType == tt);
      if (f.TargetKey is { } tk) q = q.Where(r => r.cLogAuditTargetKey == tk);
      if (f.DocNo is { } dn) q = q.Where(r => r.cLogAuditDocNo == dn);
      if (f.ActorUserId is { } au) q = q.Where(r => r.cLogAuditActorUserId == au);
      if (f.SessionId is { } si) q = q.Where(r => r.cLogAuditSessionId == si);
      if (f.From is { } from) { var utc = Utc(from); q = q.Where(r => r.cLogAuditTime >= utc); }
      if (f.To is { } to) { var utc = Utc(to); q = q.Where(r => r.cLogAuditTime < utc); }
      return q;
   }

   private static DateTime Utc(DateTime value) => value.Kind switch {
      DateTimeKind.Utc => value,
      DateTimeKind.Local => value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
   };

   internal AuditActivity ToActivity(ta_LogAudit r) => new() {
      Id = r.cLogAuditId, Time = DateTime.SpecifyKind(r.cLogAuditTime, DateTimeKind.Utc), Group = r.cLogAuditGroup, Kind = r.cLogAuditKind,
      KindLabel = catalog.LabelOf(r.cLogAuditGroup, r.cLogAuditKind), ActorType = r.cLogAuditActorType,
      ActorUserId = r.cLogAuditActorUserId, ActorAccount = r.cLogAuditActorAccount, ActingAs = r.cLogAuditActingAs,
      TargetType = r.cLogAuditTargetType, TargetKey = r.cLogAuditTargetKey, DocNo = r.cLogAuditDocNo, Summary = r.cLogAuditSummary,
      Reason = r.cLogAuditReason, Snapshot = r.cLogAuditSnapshot, Detail = r.cLogAuditDetail, Source = r.cLogAuditSource,
      App = r.cLogAuditApp, ClientAddress = r.cLogAuditClientAddress, RequestId = r.cLogAuditRequestId, SessionId = r.cLogAuditSessionId,
      Changes = string.IsNullOrEmpty(r.cLogAuditChanges) ? [] : TryChanges(r.cLogAuditChanges)
   };

   private static AuditFieldChange[] TryChanges(string json) {
      try { return JsonSerializer.Deserialize<AuditFieldChange[]>(json, Json) ?? []; }
      catch (JsonException) { return []; }
   }

   /// <summary>One place audit rows can be read from.</summary>
   internal sealed record Source(string Name, Func<Microsoft.EntityFrameworkCore.DbContext> Create, Func<Microsoft.EntityFrameworkCore.DbContext, IQueryable<ta_LogAudit>> Query);

   /// <summary>Stores (every row), then the outboxes that are not the write store (rows not moved yet).</summary>
   internal IEnumerable<Source> Sources() {
      foreach (var store in stores.All)
         yield return new Source($"Log store '{store.Name}'", store.CreateContext, db => ((LogStoreDbContext)db).Audit);
      foreach (var outbox in stores.AuditSources.Where(s => !stores.IsStoreDatabase(s.ConnectionString)))
         yield return new Source($"Audit outbox '{outbox.Name}'", () => LogOutboxDbContext.Create(outbox),
            db => ((LogOutboxDbContext)db).Audit.Where(r => r.cLogAuditShipState == 1));
   }

   internal static string EncodeCursor(DateTime time, string id) =>
      Convert.ToBase64String(Encoding.UTF8.GetBytes($"{time.Ticks}|{id}"));

   internal static (DateTime Time, string Id)? DecodeCursor(string? cursor) {
      if (string.IsNullOrWhiteSpace(cursor)) return null;
      try {
         var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
         var bar = text.IndexOf('|');
         return (new DateTime(long.Parse(text[..bar]), DateTimeKind.Utc), text[(bar + 1)..]);
      }
      catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) {
         throw new Em.Shared.ActionException("The cursor is not valid.", 400);
      }
   }
}
