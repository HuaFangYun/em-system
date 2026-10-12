using Em.Api.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core.Log.Audit;

/// <summary>
/// Moves audit entries from application databases (outbox, <c>ShipState = 1</c>) to the write store when the store
/// is another database, then deletes them from the source. Idempotent per entry id; a failure leaves the rows in
/// place to be retried. Does nothing when the store is the core database.
/// </summary>
internal sealed class AuditShipper(LogStoreRegistry stores, ILogger<AuditShipper> logger) : BackgroundService
{
   private const int BatchSize = 500;
   private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
   private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

   /// <summary>Entries waiting in all outboxes, as last counted.</summary>
   public static long Pending { get; private set; }

   protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      while (!stoppingToken.IsCancellationRequested) {
         try {
            using (LogGuard.Suppress()) Pending = await ShipAllAsync(stoppingToken);
         }
         catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
         catch (Exception ex) {
            logger.LogWarning("Audit shipper failed: {Reason}", ex.Message);
         }
         try { await Task.Delay(Interval, stoppingToken); }
         catch (OperationCanceledException) { return; }
      }
   }

   /// <summary>Ships every outbox once; returns the entries still waiting.</summary>
   internal async Task<long> ShipAllAsync(CancellationToken ct) {
      long waiting = 0;
      foreach (var source in stores.AuditSources.Where(s => !stores.IsStoreDatabase(s.ConnectionString))) {
         try {
            while (await ShipBatchAsync(source, ct) == BatchSize) { }
            await using var db = LogOutboxDbContext.Create(source);
            waiting += await db.Audit.LongCountAsync(r => r.cLogAuditShipState == 1, ct);
            _warned.Remove(source.Name);
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            if (_warned.Add(source.Name))
               logger.LogWarning("Audit entries of '{Source}' cannot be moved to the log store yet: {Reason}", source.Name, ex.GetBaseException().Message);
         }
      }
      return waiting;
   }

   private async Task<int> ShipBatchAsync(DbConnectionInfo source, CancellationToken ct) {
      await using var from = LogOutboxDbContext.Create(source);
      var rows = await from.Audit.AsNoTracking().Where(r => r.cLogAuditShipState == 1).OrderBy(r => r.cLogAuditSeq).Take(BatchSize).ToListAsync(ct);
      if (rows.Count == 0) return 0;
      var ids = rows.Select(r => r.cLogAuditId).ToArray();
      await using (var to = stores.Write.CreateContext()) {
         var present = await to.Audit.Where(r => ids.Contains(r.cLogAuditId)).Select(r => r.cLogAuditId).ToListAsync(ct);
         foreach (var row in rows.Where(r => !present.Contains(r.cLogAuditId))) {
            row.cLogAuditSeq = 0;
            row.cLogAuditShipState = 0;
            // Times are stored as UTC everywhere, but SQL Server and MySQL hand them back without a kind, which
            // PostgreSQL refuses.
            row.cLogAuditTime = DateTime.SpecifyKind(row.cLogAuditTime, DateTimeKind.Utc);
            to.Audit.Add(row);
         }
         await to.SaveChangesAsync(ct);
      }
      await from.Audit.Where(r => ids.Contains(r.cLogAuditId)).ExecuteDeleteAsync(ct);
      return rows.Count;
   }
}
