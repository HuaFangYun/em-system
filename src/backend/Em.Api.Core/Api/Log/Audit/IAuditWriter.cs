using Em.Api.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log.Audit;

/// <summary>One audit entry to write. Actor, time, address, request and session are filled by the engine.</summary>
public sealed class AuditEntryDraft
{
   /// <summary>Audit group registered with <c>AddAudit</c>, e.g. <c>Invoice</c>.</summary>
   public required string Group { get; init; }

   /// <summary>Target key; use <see cref="AuditKey"/> for EF entities.</summary>
   public required string Key { get; init; }

   /// <summary>Kind registered in the group, e.g. <c>created</c>.</summary>
   public required string Kind { get; init; }

   /// <summary>Target type; defaults to the group name.</summary>
   public string? TargetType { get; init; }

   /// <summary>Document number shown to users.</summary>
   public string? DocNo { get; init; }

   /// <summary>Summary (English).</summary>
   public string? Summary { get; init; }

   /// <summary>Reason; required for kinds registered with <c>requireReason</c>.</summary>
   public string? Reason { get; init; }

   /// <summary>Field changes, e.g. from <see cref="AuditDiff.Of{T}"/>. Secret changes are redacted again on write.</summary>
   public IReadOnlyList<AuditFieldChange>? Changes { get; init; }

   /// <summary>Snapshot object serialized as JSON (optional).</summary>
   public object? Snapshot { get; init; }

   /// <summary>Small free detail object serialized as JSON (optional).</summary>
   public object? Detail { get; init; }

   /// <summary>Overrides the actor type taken from the request (e.g. <c>anonymous</c> for a failed sign-in).</summary>
   public string? ActorType { get; init; }

   /// <summary>Overrides the actor user id (e.g. the user who just signed in).</summary>
   public string? ActorUserId { get; init; }

   /// <summary>Overrides the actor account.</summary>
   public string? ActorAccount { get; init; }

   /// <summary>Overrides the session id (e.g. the session that was just opened).</summary>
   public string? SessionId { get; init; }

   /// <summary><c>client</c> for entries reported by a client; set by the engine only.</summary>
   internal string Source { get; init; } = "server";
}

/// <summary>
/// Writes audit entries. Scoped per request. <see cref="AddEntry(AuditEntryDraft)"/> only parks the entry; the
/// engine writes it on the next <c>SaveChanges</c> of an engine context in the same request, inside the same
/// transaction. When more than one context saves in one request, direct entries with <see cref="Use"/>.
/// </summary>
/// <remarks>
/// Entries must be added before <c>SaveChanges</c>. Unknown groups or kinds, and a missing required reason, throw
/// right away, before any data is saved. An entry still parked when the request ends is reported as an error
/// ("audit entry not saved"). Guide: <c>doc/engine/engine-log.md</c>.
/// </remarks>
public interface IAuditWriter
{
   /// <summary>Parks an entry for the next <c>SaveChanges</c>.</summary>
   void AddEntry(AuditEntryDraft entry);

   /// <summary>Parks an entry for the next <c>SaveChanges</c>.</summary>
   void AddEntry(string group, string key, string kind, string? docNo = null, string? summary = null, string? reason = null,
      IReadOnlyList<AuditFieldChange>? changes = null, object? snapshot = null, object? detail = null);

   /// <summary>A writer whose entries go only to <paramref name="context"/>'s next <c>SaveChanges</c>.</summary>
   IAuditWriter Use(DbContext context);

   /// <summary>
   /// Writes an entry now in its own transaction to the core database, for events that change no data or
   /// belong to a request that fails (a refused sign-in).
   /// </summary>
   Task WriteNowAsync(AuditEntryDraft entry, CancellationToken ct = default);

   /// <summary>Directs every parked entry to <paramref name="context"/> and saves it (with its other pending changes).</summary>
   Task SaveAsync(DbContext context, CancellationToken ct = default);

   /// <summary>Entries parked and not written yet.</summary>
   int PendingCount { get; }
}
