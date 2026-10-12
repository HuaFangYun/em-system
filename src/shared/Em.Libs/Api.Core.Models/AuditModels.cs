namespace Em.Api.Core.Models;

/// <summary>Who performed an audited event.</summary>
public static class AuditActorTypes
{
   /// <summary>A signed-in user.</summary>
   public const string User = "user";

   /// <summary>A robot token.</summary>
   public const string Robot = "robot";

   /// <summary>A developer debug token (with <c>ActingAs</c> when impersonating).</summary>
   public const string Debug = "debug";

   /// <summary>A background job of the server.</summary>
   public const string System = "system";

   /// <summary>A caller without a proven identity, e.g. a failed sign-in.</summary>
   public const string Anonymous = "anonymous";
}

/// <summary>One field change of an audit entry.</summary>
public sealed class AuditFieldChange
{
   /// <summary>Row label for child rows; empty for the header.</summary>
   public string? Row { get; set; }

   /// <summary>Field (column) name.</summary>
   public string Field { get; set; } = string.Empty;

   /// <summary>Display label of the field.</summary>
   public string? Label { get; set; }

   /// <summary>Old raw value as text; <c>null</c> for an insert.</summary>
   public string? Old { get; set; }

   /// <summary>New raw value as text; <c>null</c> for a delete.</summary>
   public string? New { get; set; }

   /// <summary>Display text of the old value (e.g. a name for an id), when the module supplied one.</summary>
   public string? OldText { get; set; }

   /// <summary>Display text of the new value, when the module supplied one.</summary>
   public string? NewText { get; set; }

   /// <summary>True for a secret field: only <c>(set)</c>/<c>(changed)</c> is recorded, never the value.</summary>
   public bool Secret { get; set; }
}

/// <summary>One audit entry as read by module screens and the Log Manager.</summary>
public sealed class AuditActivity
{
   /// <summary>ULID of the entry.</summary>
   public string Id { get; set; } = string.Empty;

   /// <summary>When it happened (UTC).</summary>
   public DateTime Time { get; set; }

   /// <summary>Audit group, e.g. <c>Invoice</c> or <c>System.Auth</c>.</summary>
   public string Group { get; set; } = string.Empty;

   /// <summary>Event kind, e.g. <c>created</c>.</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>Label of the kind from the catalog.</summary>
   public string? KindLabel { get; set; }

   /// <summary>Actor type; see <see cref="AuditActorTypes"/>.</summary>
   public string ActorType { get; set; } = AuditActorTypes.User;

   /// <summary>Actor user id.</summary>
   public string? ActorUserId { get; set; }

   /// <summary>Actor account name.</summary>
   public string? ActorAccount { get; set; }

   /// <summary>Account the actor acted as.</summary>
   public string? ActingAs { get; set; }

   /// <summary>Target type, e.g. <c>user</c>.</summary>
   public string? TargetType { get; set; }

   /// <summary>Target key (EF primary key; composite keys as an ordered JSON array).</summary>
   public string? TargetKey { get; set; }

   /// <summary>Document number shown to users.</summary>
   public string? DocNo { get; set; }

   /// <summary>Summary text.</summary>
   public string? Summary { get; set; }

   /// <summary>Reason given by the actor.</summary>
   public string? Reason { get; set; }

   /// <summary>Field changes.</summary>
   public AuditFieldChange[] Changes { get; set; } = [];

   /// <summary>Snapshot JSON, when the module recorded one.</summary>
   public string? Snapshot { get; set; }

   /// <summary>Small free JSON detail.</summary>
   public string? Detail { get; set; }

   /// <summary><c>server</c> or <c>client</c> (reported by a client, not proven by the server).</summary>
   public string Source { get; set; } = "server";

   /// <summary>Application name.</summary>
   public string? App { get; set; }

   /// <summary>Client address.</summary>
   public string? ClientAddress { get; set; }

   /// <summary>Request id; links to the telemetry of the same request.</summary>
   public string? RequestId { get; set; }

   /// <summary>Session id.</summary>
   public string? SessionId { get; set; }
}

/// <summary>One page of audit activity.</summary>
public sealed class AuditActivityPage
{
   /// <summary>Entries, newest first.</summary>
   public AuditActivity[] Items { get; set; } = [];

   /// <summary>Opaque cursor for the next page (<c>null</c> = no more). Clients pass it back unchanged.</summary>
   public string? NextCursor { get; set; }

   /// <summary>Sources that could not be read; the page is still valid without them.</summary>
   public string[] Warnings { get; set; } = [];
}

/// <summary>One event kind of an audit group in the catalog.</summary>
public sealed class AuditCatalogEntry
{
   /// <summary>Kind, e.g. <c>voided</c>.</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>Display label.</summary>
   public string Label { get; set; } = string.Empty;

   /// <summary>True when a reason is required.</summary>
   public bool RequireReason { get; set; }

   /// <summary>True when clients may report this kind through <c>PostMeta_AuditEntry</c>.</summary>
   public bool FromClient { get; set; }
}

/// <summary>One audit group in the catalog.</summary>
public sealed class AuditCatalogGroup
{
   /// <summary>Group name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Display title.</summary>
   public string Title { get; set; } = string.Empty;

   /// <summary>True for an engine group (<c>System.*</c>).</summary>
   public bool IsSystem { get; set; }

   /// <summary>Shortest retention the group allows, in months; <c>null</c> = no minimum.</summary>
   public int? MinRetentionMonths { get; set; }

   /// <summary>The event kinds.</summary>
   public AuditCatalogEntry[] Entries { get; set; } = [];
}
