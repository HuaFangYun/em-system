using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log;

/// <summary>
/// Context of a log store (<c>tables/071-log-store.sql</c>): telemetry events, error groups, ingest batches,
/// audit entries and the schema version. Created by <see cref="LogStoreRegistry"/> per store, not through DI,
/// because the read stores are only known at runtime.
/// </summary>
internal sealed class LogStoreDbContext(DbContextOptions<LogStoreDbContext> options) : EmDbContext(options)
{
   /// <summary>Schema version this engine writes and reads.</summary>
   public const int SchemaVersion = 1;

   public DbSet<ta_LogEvent> Events => Set<ta_LogEvent>();
   public DbSet<ta_LogError> Errors => Set<ta_LogError>();
   public DbSet<ta_LogIngestBatch> Batches => Set<ta_LogIngestBatch>();
   public DbSet<ta_LogAudit> Audit => Set<ta_LogAudit>();
   public DbSet<ta_LogSchema> Schema => Set<ta_LogSchema>();

   protected override void OnModelCreating(ModelBuilder b) {
      base.OnModelCreating(b);
      b.Entity<ta_LogEvent>(e => {
         e.HasKey(x => x.cLogEventSeq);
         e.Property(x => x.cLogEventSeq).ValueGeneratedOnAdd();
         e.Property(x => x.cLogEventCategory).HasMaxLength(200);
         e.Property(x => x.cLogEventTemplate).HasMaxLength(1000);
         e.Property(x => x.cLogEventMessage).HasMaxLength(4000);
         e.Property(x => x.cLogEventFingerprint).HasMaxLength(32).IsUnicode(false);
         e.Property(x => x.cLogEventApp).HasMaxLength(100);
         e.Property(x => x.cLogEventAppVersion).HasMaxLength(50);
         e.Property(x => x.cLogEventInstance).HasMaxLength(100);
         e.Property(x => x.cLogEventEnvironment).HasMaxLength(50);
         e.Property(x => x.cLogEventTenant).HasMaxLength(100);
         e.Property(x => x.cLogEventTraceId).HasMaxLength(64).IsUnicode(false);
         e.Property(x => x.cLogEventSpanId).HasMaxLength(32).IsUnicode(false);
         e.Property(x => x.cLogEventRequestId).HasMaxLength(100).IsUnicode(false);
         e.Property(x => x.cLogEventSessionId).HasMaxLength(26).IsUnicode(false);
         e.Property(x => x.cLogEventUserId).HasMaxLength(26).IsUnicode(false);
         e.Property(x => x.cLogEventAccount).HasMaxLength(100);
         e.Property(x => x.cLogEventActingAs).HasMaxLength(100);
         e.Property(x => x.cLogEventClientAddress).HasMaxLength(64).IsUnicode(false);
         e.Property(x => x.cLogEventSource).HasMaxLength(10).IsUnicode(false);
         e.Property(x => x.cLogEventSenderId).HasMaxLength(100);
      });
      b.Entity<ta_LogError>(e => {
         e.Property(x => x.cLogErrorId).HasMaxLength(26).IsUnicode(false).IsFixedLength();
         e.Property(x => x.cLogErrorFingerprint).HasMaxLength(32).IsUnicode(false);
         e.HasIndex(x => x.cLogErrorFingerprint).IsUnique();
         e.Property(x => x.cLogErrorType).HasMaxLength(300);
         e.Property(x => x.cLogErrorTopFrame).HasMaxLength(1000);
         e.Property(x => x.cLogErrorMessage).HasMaxLength(2000);
         e.Property(x => x.cLogErrorLastApp).HasMaxLength(100);
         e.Property(x => x.cLogErrorLastAppVersion).HasMaxLength(50);
      });
      b.Entity<ta_LogIngestBatch>(e => {
         e.Property(x => x.cLogIngestBatchId).HasMaxLength(64).IsUnicode(false);
         e.Property(x => x.cLogIngestBatchSender).HasMaxLength(100);
      });
   }
}
