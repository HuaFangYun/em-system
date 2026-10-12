using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core.Log;

/// <summary>
/// Startup check of the log engine: the write store has the store tables at the engine's schema version, and every
/// audit outbox database has <c>ta_LogAudit</c>. Stops startup with a message naming the script, like the object
/// storage check. Read stores that cannot be opened only produce a warning.
/// </summary>
internal sealed class LogStartupCheck(LogStoreRegistry stores, ILogger<LogStartupCheck> logger) : IHostedService
{
   public async Task StartAsync(CancellationToken ct) {
      using var _ = LogGuard.Suppress();
      try {
         await CheckStoreAsync(stores.Write, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException) {
         throw new InvalidOperationException(
            $"Log store '{stores.Write.Name}' is not ready: {ex.GetBaseException().Message} Run doc/sqlscript/mssql/tables/071-log-store.sql " +
            "(or Prepare it from the Log Manager), or remove AddLogServer() from the host.", ex);
      }

      foreach (var source in stores.AuditSources) {
         try {
            await using var db = LogOutboxDbContext.Create(source);
            await db.Audit.OrderBy(a => a.cLogAuditId).Select(a => a.cLogAuditId).FirstOrDefaultAsync(ct);
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException(
               $"Database connection '{source.Name}' has no usable ta_LogAudit table: {ex.GetBaseException().Message} " +
               "Run doc/sqlscript/mssql/tables/070-log-audit.sql on that database.", ex);
         }
      }

      foreach (var store in stores.Read) {
         try { await CheckStoreAsync(store, ct); }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning("Log read store '{Store}' is not available: {Reason}", store.Name, ex.GetBaseException().Message);
         }
      }
   }

   /// <summary>Throws when the store lacks a table or has another schema version.</summary>
   internal static async Task<int> CheckStoreAsync(LogStoreHandle store, CancellationToken ct) {
      await using var db = store.CreateContext();
      var version = await db.Schema.Where(s => s.cLogSchemaId == 1).Select(s => (int?)s.cLogSchemaVersion).FirstOrDefaultAsync(ct)
         ?? throw new InvalidOperationException("ta_LogSchema has no version row.");
      if (version != LogStoreDbContext.SchemaVersion)
         throw new InvalidOperationException($"Store schema version {version} differs from the engine's version {LogStoreDbContext.SchemaVersion}.");
      // Ordered only to keep EF from warning about an unordered First; one row is enough to prove each table.
      await db.Events.OrderBy(e => e.cLogEventSeq).Select(e => e.cLogEventSeq).FirstOrDefaultAsync(ct);
      await db.Errors.OrderBy(e => e.cLogErrorId).Select(e => e.cLogErrorId).FirstOrDefaultAsync(ct);
      await db.Batches.OrderBy(e => e.cLogIngestBatchId).Select(e => e.cLogIngestBatchId).FirstOrDefaultAsync(ct);
      await db.Audit.OrderBy(e => e.cLogAuditId).Select(e => e.cLogAuditId).FirstOrDefaultAsync(ct);
      return version;
   }

   public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
