using System.Text.Json;
using Em.Api.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log;

/// <summary>
/// The only way telemetry enters the store, whatever its source: the server's own logger, the client ingest
/// action, and later an OTLP wrapper. Knows no protocol.
/// </summary>
public interface ILogIngest
{
   /// <summary>
   /// Validates, enriches and writes events to the write store, and updates the error groups. Throws when the
   /// store cannot be written; the caller decides whether to keep the events elsewhere.
   /// </summary>
   /// <param name="events">Events to write.</param>
   /// <param name="sender">Who handed them over.</param>
   /// <param name="senderId">User id or robot name of the sender; <c>null</c> for the process.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The number of events written.</returns>
   Task<int> WriteAsync(IReadOnlyList<LogEvent> events, LogSender sender, string? senderId, CancellationToken ct);
}

/// <summary>Implementation of <see cref="ILogIngest"/> on the write store of <see cref="LogStoreRegistry"/>.</summary>
internal sealed class LogIngest(LogStoreRegistry stores, LogRuntime runtime) : ILogIngest
{
   private const int MaxUsersPerError = 200;
   private static readonly SemaphoreSlim Gate = new(1, 1);

   public Task<int> WriteAsync(IReadOnlyList<LogEvent> events, LogSender sender, string? senderId, CancellationToken ct) =>
      WriteAsync(events, sender, senderId, null, ct);

   /// <summary>
   /// Writes the events together with the row of the client batch they came in, in one transaction, so a batch is
   /// only marked received when its events are stored. Returns -1 when the batch was already received.
   /// </summary>
   internal async Task<int> WriteAsync(IReadOnlyList<LogEvent> events, LogSender sender, string? senderId, ta_LogIngestBatch? batch,
      CancellationToken ct) {
      if (events.Count == 0 && batch is null) return 0;
      using var _ = LogGuard.Suppress();
      var received = DateTime.UtcNow;
      var rows = new List<ta_LogEvent>(events.Count);
      foreach (var e in events) {
         Normalize(e, received);
         rows.Add(ToRow(e, sender, senderId, received));
      }

      await Gate.WaitAsync(ct);
      try {
         await using var db = stores.Write.CreateContext();
         if (batch is not null) {
            if (await db.Batches.AnyAsync(b => b.cLogIngestBatchId == batch.cLogIngestBatchId, ct)) return -1;
            db.Batches.Add(batch);
         }
         db.Events.AddRange(rows);
         await UpsertErrorsAsync(db, events.Where(e => e.Kind == LogEventKind.Error && e.Fingerprint is not null).ToList(), ct);
         try {
            await db.SaveChangesAsync(ct);
         }
         catch (DbUpdateException) when (batch is not null) {
            // The same batch arrived twice at once; the other copy was stored and this transaction rolled back.
            if (await IsReceivedAsync(batch.cLogIngestBatchId, ct)) return -1;
            throw;
         }
         runtime.ReportStore(null);
         return rows.Count;
      }
      catch (Exception ex) when (ex is not OperationCanceledException) {
         runtime.ReportStore(ex.Message);
         throw;
      }
      finally {
         Gate.Release();
      }
   }

   private async Task<bool> IsReceivedAsync(string batchId, CancellationToken ct) {
      await using var db = stores.Write.CreateContext();
      return await db.Batches.AnyAsync(b => b.cLogIngestBatchId == batchId, ct);
   }

   /// <summary>Fills defaults and trims fields to the column sizes.</summary>
   internal static void Normalize(LogEvent e, DateTime received) {
      if (e.Time == default || e.Time > received.AddMinutes(10)) e.Time = received;
      if (e.Time.Kind != DateTimeKind.Utc) e.Time = DateTime.SpecifyKind(e.Time.Kind == DateTimeKind.Local ? e.Time.ToUniversalTime() : e.Time, DateTimeKind.Utc);
      if (!Enum.IsDefined(e.Level)) e.Level = LogEventLevel.Information;
      if (!Enum.IsDefined(e.Kind)) e.Kind = LogEventKind.Diagnostic;
      if (e.Exception is not null && e.Level >= LogEventLevel.Error && e.Kind == LogEventKind.Diagnostic) e.Kind = LogEventKind.Error;
      e.Category = Cut(e.Category, 200) ?? "";
      e.Template = Cut(e.Template, 1000);
      e.Message = Cut(e.Message, 4000) ?? "";
      if (e.Properties is { Length: > 16 * 1024 }) e.Properties = null;
      e.Exception = Cut(e.Exception, 32 * 1024);
      if (e.Kind == LogEventKind.Error && e.Exception is { } text) e.Fingerprint ??= ErrorFingerprint.Compute(text);
      e.Fingerprint = Cut(e.Fingerprint, 32);
      e.App = Cut(e.App, 100);
      e.AppVersion = Cut(e.AppVersion, 50);
      e.Instance = Cut(e.Instance, 100);
      e.Environment = Cut(e.Environment, 50);
      e.Tenant = Cut(e.Tenant, 100);
      e.TraceId = Cut(e.TraceId, 64);
      e.SpanId = Cut(e.SpanId, 32);
      e.RequestId = Cut(e.RequestId, 100);
      e.SessionId = Cut(e.SessionId, 26);
      e.UserId = Cut(e.UserId, 26);
      e.Account = Cut(e.Account, 100);
      e.ActingAs = Cut(e.ActingAs, 100);
      e.ClientAddress = Cut(e.ClientAddress, 64);
      e.Source = e.Source == "client" ? "client" : "server";
   }

   private static string? Cut(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

   internal static ta_LogEvent ToRow(LogEvent e, LogSender sender, string? senderId, DateTime received) => new() {
      cLogEventTime = e.Time, cLogEventKind = (int)e.Kind, cLogEventLevel = (int)e.Level, cLogEventCategory = e.Category,
      cLogEventTemplate = e.Template, cLogEventMessage = e.Message, cLogEventProperties = e.Properties,
      cLogEventException = e.Exception, cLogEventFingerprint = e.Fingerprint, cLogEventApp = e.App,
      cLogEventAppVersion = e.AppVersion, cLogEventInstance = e.Instance, cLogEventEnvironment = e.Environment,
      cLogEventTenant = e.Tenant, cLogEventTraceId = e.TraceId, cLogEventSpanId = e.SpanId, cLogEventRequestId = e.RequestId,
      cLogEventSessionId = e.SessionId, cLogEventUserId = e.UserId, cLogEventAccount = e.Account, cLogEventActingAs = e.ActingAs,
      cLogEventClientAddress = e.ClientAddress, cLogEventSource = e.Source, cLogEventSender = (int)sender,
      cLogEventSenderId = Cut(senderId, 100), cLogEventReceived = received
   };

   internal static LogEvent FromRow(ta_LogEvent r) => new() {
      Seq = r.cLogEventSeq, Time = DateTime.SpecifyKind(r.cLogEventTime, DateTimeKind.Utc), Kind = (LogEventKind)r.cLogEventKind,
      Level = (LogEventLevel)r.cLogEventLevel, Category = r.cLogEventCategory, Template = r.cLogEventTemplate,
      Message = r.cLogEventMessage, Properties = r.cLogEventProperties, Exception = r.cLogEventException,
      Fingerprint = r.cLogEventFingerprint, App = r.cLogEventApp, AppVersion = r.cLogEventAppVersion,
      Instance = r.cLogEventInstance, Environment = r.cLogEventEnvironment, Tenant = r.cLogEventTenant,
      TraceId = r.cLogEventTraceId, SpanId = r.cLogEventSpanId, RequestId = r.cLogEventRequestId,
      SessionId = r.cLogEventSessionId, UserId = r.cLogEventUserId, Account = r.cLogEventAccount,
      ActingAs = r.cLogEventActingAs, ClientAddress = r.cLogEventClientAddress, Source = r.cLogEventSource
   };

   private static async Task UpsertErrorsAsync(LogStoreDbContext db, List<LogEvent> errors, CancellationToken ct) {
      if (errors.Count == 0) return;
      var fingerprints = errors.Select(e => e.Fingerprint!).Distinct().ToArray();
      var existing = await db.Errors.AsTracking().Where(r => fingerprints.Contains(r.cLogErrorFingerprint)).ToDictionaryAsync(r => r.cLogErrorFingerprint, ct);
      var now = DateTime.UtcNow;
      foreach (var group in errors.GroupBy(e => e.Fingerprint!)) {
         var last = group.MaxBy(e => e.Time)!;
         var first = group.Min(e => e.Time);
         if (!existing.TryGetValue(group.Key, out var row)) {
            row = new ta_LogError {
               cLogErrorId = Ulid.NewUlid().ToString(), cLogErrorFingerprint = group.Key,
               cLogErrorType = ErrorFingerprint.TypeOf(last.Exception!), cLogErrorTopFrame = ErrorFingerprint.TopFrameOf(last.Exception!),
               cLogErrorFirstSeen = first, cLogErrorLastSeen = last.Time, cLogErrorState = (int)LogErrorState.New,
               datestamp = now, ustamp = now
            };
            db.Errors.Add(row);
            existing[group.Key] = row;
         }
         else {
            if (row.cLogErrorState == (int)LogErrorState.Resolved) row.cLogErrorState = (int)LogErrorState.New;
            if (last.Time > row.cLogErrorLastSeen) row.cLogErrorLastSeen = last.Time;
            row.ustamp = now;
         }
         row.cLogErrorCount += group.Count();
         row.cLogErrorMessage = Cut(last.Message, 2000);
         row.cLogErrorLastApp = last.App;
         row.cLogErrorLastAppVersion = last.AppVersion;
         var users = string.IsNullOrEmpty(row.cLogErrorUsers) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(row.cLogErrorUsers) ?? [];
         foreach (var user in group.Select(e => e.UserId).Where(u => !string.IsNullOrEmpty(u)).Distinct()) {
            if (users.Count >= MaxUsersPerError) break;
            if (!users.Contains(user!)) users.Add(user!);
         }
         row.cLogErrorUsers = JsonSerializer.Serialize(users);
         row.cLogErrorUserCount = users.Count;
      }
   }
}
