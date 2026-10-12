using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log;

/// <summary>Row of <c>ta_LogAudit</c>: an audit entry, in an outbox (application database) or in the store.</summary>
[Table("ta_LogAudit")]
internal sealed class ta_LogAudit
{
   [Key] public string cLogAuditId { get; set; } = string.Empty;
   [DatabaseGenerated(DatabaseGeneratedOption.Identity)] public long cLogAuditSeq { get; set; }
   public string cLogAuditGroup { get; set; } = string.Empty;
   public string cLogAuditKind { get; set; } = string.Empty;
   public DateTime cLogAuditTime { get; set; }
   public string cLogAuditActorType { get; set; } = string.Empty;
   public string? cLogAuditActorUserId { get; set; }
   public string? cLogAuditActorAccount { get; set; }
   public string? cLogAuditActingAs { get; set; }
   public string? cLogAuditTargetType { get; set; }
   public string? cLogAuditTargetKey { get; set; }
   public string? cLogAuditDocNo { get; set; }
   public string? cLogAuditSummary { get; set; }
   public string? cLogAuditReason { get; set; }
   public string? cLogAuditChanges { get; set; }
   public string? cLogAuditSnapshot { get; set; }
   public string? cLogAuditDetail { get; set; }
   public string cLogAuditSource { get; set; } = "server";
   public string? cLogAuditApp { get; set; }
   public string? cLogAuditInstance { get; set; }
   public string? cLogAuditClientAddress { get; set; }
   public string? cLogAuditRequestId { get; set; }
   public string? cLogAuditTraceId { get; set; }
   public string? cLogAuditSessionId { get; set; }
   /// <summary>0 = in the store (or the store itself), 1 = waiting to be moved to the store.</summary>
   public int cLogAuditShipState { get; set; }

   /// <summary>Maps the entity; called by every engine context so audit entries can join its transaction.</summary>
   internal static void Configure(ModelBuilder b) {
      b.Entity<ta_LogAudit>(e => {
         e.HasKey(x => x.cLogAuditId);
         e.Property(x => x.cLogAuditId).HasMaxLength(26).IsUnicode(false).IsFixedLength();
         e.Property(x => x.cLogAuditSeq).ValueGeneratedOnAdd();
         e.Property(x => x.cLogAuditSeq).Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
         e.Property(x => x.cLogAuditGroup).HasMaxLength(100).IsUnicode(false);
         e.Property(x => x.cLogAuditKind).HasMaxLength(100).IsUnicode(false);
         e.Property(x => x.cLogAuditActorType).HasMaxLength(20).IsUnicode(false);
         e.Property(x => x.cLogAuditActorUserId).HasMaxLength(26).IsUnicode(false);
         e.Property(x => x.cLogAuditActorAccount).HasMaxLength(100);
         e.Property(x => x.cLogAuditActingAs).HasMaxLength(100);
         e.Property(x => x.cLogAuditTargetType).HasMaxLength(100).IsUnicode(false);
         e.Property(x => x.cLogAuditTargetKey).HasMaxLength(400);
         e.Property(x => x.cLogAuditDocNo).HasMaxLength(100);
         e.Property(x => x.cLogAuditSummary).HasMaxLength(1000);
         e.Property(x => x.cLogAuditReason).HasMaxLength(1000);
         e.Property(x => x.cLogAuditSource).HasMaxLength(10).IsUnicode(false);
         e.Property(x => x.cLogAuditApp).HasMaxLength(100);
         e.Property(x => x.cLogAuditInstance).HasMaxLength(100);
         e.Property(x => x.cLogAuditClientAddress).HasMaxLength(64).IsUnicode(false);
         e.Property(x => x.cLogAuditRequestId).HasMaxLength(100).IsUnicode(false);
         e.Property(x => x.cLogAuditTraceId).HasMaxLength(64).IsUnicode(false);
         e.Property(x => x.cLogAuditSessionId).HasMaxLength(26).IsUnicode(false);
      });
   }
}

/// <summary>Row of <c>ta_LogEvent</c>: one telemetry event in the store.</summary>
[Table("ta_LogEvent")]
internal sealed class ta_LogEvent
{
   [Key] public long cLogEventSeq { get; set; }
   public DateTime cLogEventTime { get; set; }
   public int cLogEventKind { get; set; }
   public int cLogEventLevel { get; set; }
   public string cLogEventCategory { get; set; } = string.Empty;
   public string? cLogEventTemplate { get; set; }
   public string cLogEventMessage { get; set; } = string.Empty;
   public string? cLogEventProperties { get; set; }
   public string? cLogEventException { get; set; }
   public string? cLogEventFingerprint { get; set; }
   public string? cLogEventApp { get; set; }
   public string? cLogEventAppVersion { get; set; }
   public string? cLogEventInstance { get; set; }
   public string? cLogEventEnvironment { get; set; }
   public string? cLogEventTenant { get; set; }
   public string? cLogEventTraceId { get; set; }
   public string? cLogEventSpanId { get; set; }
   public string? cLogEventRequestId { get; set; }
   public string? cLogEventSessionId { get; set; }
   public string? cLogEventUserId { get; set; }
   public string? cLogEventAccount { get; set; }
   public string? cLogEventActingAs { get; set; }
   public string? cLogEventClientAddress { get; set; }
   public string cLogEventSource { get; set; } = "server";
   public int cLogEventSender { get; set; }
   public string? cLogEventSenderId { get; set; }
   public DateTime cLogEventReceived { get; set; }
}

/// <summary>Row of <c>ta_LogError</c>: errors grouped by fingerprint.</summary>
[Table("ta_LogError")]
internal sealed class ta_LogError
{
   [Key] public string cLogErrorId { get; set; } = string.Empty;
   public string cLogErrorFingerprint { get; set; } = string.Empty;
   public string cLogErrorType { get; set; } = string.Empty;
   public string? cLogErrorTopFrame { get; set; }
   public string? cLogErrorMessage { get; set; }
   public long cLogErrorCount { get; set; }
   public DateTime cLogErrorFirstSeen { get; set; }
   public DateTime cLogErrorLastSeen { get; set; }
   public string? cLogErrorLastApp { get; set; }
   public string? cLogErrorLastAppVersion { get; set; }
   /// <summary>JSON array of up to 200 distinct user ids, for the approximate affected-user count.</summary>
   public string? cLogErrorUsers { get; set; }
   public int cLogErrorUserCount { get; set; }
   public int cLogErrorState { get; set; }
   public DateTime ustamp { get; set; }
   public DateTime datestamp { get; set; }
   public string? json_object { get; set; }
}

/// <summary>Row of <c>ta_LogIngestBatch</c>: a received client batch, for idempotent resends.</summary>
[Table("ta_LogIngestBatch")]
internal sealed class ta_LogIngestBatch
{
   [Key] public string cLogIngestBatchId { get; set; } = string.Empty;
   public DateTime cLogIngestBatchTime { get; set; }
   public string? cLogIngestBatchSender { get; set; }
   public int cLogIngestBatchEvents { get; set; }
}

/// <summary>Row of <c>ta_LogSchema</c>: the store schema version (one row).</summary>
[Table("ta_LogSchema")]
internal sealed class ta_LogSchema
{
   [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] public int cLogSchemaId { get; set; }
   public int cLogSchemaVersion { get; set; }
   public DateTime ustamp { get; set; }
}
