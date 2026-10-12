namespace Em.Api.Core.Models;

/// <summary>Family of a telemetry event.</summary>
public enum LogEventKind
{
   /// <summary>A diagnostic line written through <c>ILogger</c>.</summary>
   Diagnostic = 0,

   /// <summary>An error or crash that carries an exception; grouped by fingerprint.</summary>
   Error = 1,

   /// <summary>A request trace line (DONE, ABORT, REJECT) of the action dispatcher.</summary>
   Access = 2
}

/// <summary>Severity of a telemetry event; the numbers match <c>Microsoft.Extensions.Logging.LogLevel</c>.</summary>
public enum LogEventLevel
{
   /// <summary>Most detailed messages.</summary>
   Trace = 0,

   /// <summary>Debugging messages.</summary>
   Debug = 1,

   /// <summary>General flow of the application.</summary>
   Information = 2,

   /// <summary>Something unexpected that did not stop the work.</summary>
   Warning = 3,

   /// <summary>A failure of the current operation.</summary>
   Error = 4,

   /// <summary>A failure that needs immediate attention.</summary>
   Critical = 5
}

/// <summary>Who handed a batch of telemetry to the ingest service.</summary>
public enum LogSender
{
   /// <summary>The API process itself, through its own <c>ILogger</c>.</summary>
   Process = 0,

   /// <summary>A signed-in user's client (WPF/MAUI) through <c>PostMeta_LogIngest</c>.</summary>
   User = 1,

   /// <summary>A robot token.</summary>
   Robot = 2
}

/// <summary>
/// One telemetry event: the shared envelope of diagnostic, error and access events. The fields map to the
/// OpenTelemetry log model, so an OTLP wrapper can be added later without changing storage.
/// </summary>
public sealed class LogEvent
{
   /// <summary>Store sequence; 0 for an event that has not been stored yet.</summary>
   public long Seq { get; set; }

   /// <summary>When the event happened, in UTC.</summary>
   public DateTime Time { get; set; }

   /// <summary>Family of the event.</summary>
   public LogEventKind Kind { get; set; }

   /// <summary>Severity.</summary>
   public LogEventLevel Level { get; set; }

   /// <summary>Logger category, e.g. <c>Em.Api.Request</c>.</summary>
   public string Category { get; set; } = string.Empty;

   /// <summary>Message template, e.g. <c>#{Id} DONE {Method} {Route}</c>.</summary>
   public string? Template { get; set; }

   /// <summary>Rendered message, as the console shows it.</summary>
   public string Message { get; set; } = string.Empty;

   /// <summary>Structured properties as a JSON object.</summary>
   public string? Properties { get; set; }

   /// <summary>Exception text (type, message and stack trace), when there is one.</summary>
   public string? Exception { get; set; }

   /// <summary>Short hash of the exception type and top frame; set for <see cref="LogEventKind.Error"/>.</summary>
   public string? Fingerprint { get; set; }

   /// <summary>Application name.</summary>
   public string? App { get; set; }

   /// <summary>Application version.</summary>
   public string? AppVersion { get; set; }

   /// <summary>Machine or instance that produced the event.</summary>
   public string? Instance { get; set; }

   /// <summary>Hosting environment, e.g. Production.</summary>
   public string? Environment { get; set; }

   /// <summary>Tenant; empty in a single installation.</summary>
   public string? Tenant { get; set; }

   /// <summary>W3C trace id, when known.</summary>
   public string? TraceId { get; set; }

   /// <summary>W3C span id, when known.</summary>
   public string? SpanId { get; set; }

   /// <summary>Unique request id (<c>instance:id:start</c>).</summary>
   public string? RequestId { get; set; }

   /// <summary>Session of the user, when known.</summary>
   public string? SessionId { get; set; }

   /// <summary>User at the moment of the event; empty before sign-in.</summary>
   public string? UserId { get; set; }

   /// <summary>Account name of <see cref="UserId"/>, when known.</summary>
   public string? Account { get; set; }

   /// <summary>Account a debug token acted as, when impersonating.</summary>
   public string? ActingAs { get; set; }

   /// <summary>Address of the client.</summary>
   public string? ClientAddress { get; set; }

   /// <summary><c>server</c> or <c>client</c>.</summary>
   public string Source { get; set; } = "server";
}

/// <summary>A batch of client telemetry sent through <c>PostMeta_LogIngest</c>.</summary>
public sealed class LogIngestBatch
{
   /// <summary>Unique id of the batch, chosen by the client; a batch that was already received is ignored.</summary>
   public string BatchId { get; set; } = string.Empty;

   /// <summary>The events.</summary>
   public LogEvent[] Events { get; set; } = [];

   /// <summary>Events the client dropped because its local folder was full, since the last batch.</summary>
   public int DroppedEvents { get; set; }
}

/// <summary>Answer of <c>PostMeta_LogIngest</c>.</summary>
public sealed class LogIngestResult
{
   /// <summary>True when the batch id was already received; nothing was written again.</summary>
   public bool Duplicate { get; set; }

   /// <summary>Events written.</summary>
   public int Accepted { get; set; }

   /// <summary>Events refused (too large or invalid).</summary>
   public int Rejected { get; set; }
}

/// <summary>One page of telemetry events.</summary>
public sealed class LogEventPage
{
   /// <summary>Events, newest first.</summary>
   public LogEvent[] Items { get; set; } = [];

   /// <summary>Opaque cursor for the next page; <c>null</c> when there is none.</summary>
   public string? NextCursor { get; set; }

   /// <summary>Sources that could not be read for this page.</summary>
   public string[] Warnings { get; set; } = [];
}

/// <summary>Triage state of an error group.</summary>
public enum LogErrorState
{
   /// <summary>Not looked at yet, or resolved and seen again.</summary>
   New = 1,

   /// <summary>Known, being looked at.</summary>
   Acknowledged = 2,

   /// <summary>Fixed; seeing it again turns it back to <see cref="New"/>.</summary>
   Resolved = 3,

   /// <summary>Not worth fixing.</summary>
   Ignored = 4
}

/// <summary>Errors grouped by fingerprint.</summary>
public sealed class LogErrorGroup
{
   /// <summary>Fingerprint of the group.</summary>
   public string Fingerprint { get; set; } = string.Empty;

   /// <summary>Exception type name.</summary>
   public string ExceptionType { get; set; } = string.Empty;

   /// <summary>Top application frame.</summary>
   public string? TopFrame { get; set; }

   /// <summary>A sample message.</summary>
   public string? SampleMessage { get; set; }

   /// <summary>How many times it happened.</summary>
   public long Count { get; set; }

   /// <summary>First time seen (UTC).</summary>
   public DateTime FirstSeen { get; set; }

   /// <summary>Last time seen (UTC).</summary>
   public DateTime LastSeen { get; set; }

   /// <summary>Application of the last occurrence.</summary>
   public string? LastApp { get; set; }

   /// <summary>Version of the last occurrence.</summary>
   public string? LastAppVersion { get; set; }

   /// <summary>Approximate number of distinct users affected.</summary>
   public int AffectedUsers { get; set; }

   /// <summary>Triage state.</summary>
   public LogErrorState State { get; set; } = LogErrorState.New;
}

/// <summary>One page of error groups.</summary>
public sealed class LogErrorPage
{
   /// <summary>Groups, most recently seen first.</summary>
   public LogErrorGroup[] Items { get; set; } = [];

   /// <summary>Opaque cursor for the next page; <c>null</c> when there is none.</summary>
   public string? NextCursor { get; set; }
}

/// <summary>An error group with its latest occurrences.</summary>
public sealed class LogErrorDetail
{
   /// <summary>The group.</summary>
   public LogErrorGroup Group { get; set; } = new();

   /// <summary>The latest occurrences, newest first (at most 20).</summary>
   public LogEvent[] Recent { get; set; } = [];
}

/// <summary>Counters for the Log Manager summary tab.</summary>
public sealed class LogSummary
{
   /// <summary>Errors in the range.</summary>
   public long Errors { get; set; }

   /// <summary>Warnings in the range.</summary>
   public long Warnings { get; set; }

   /// <summary>Error groups first seen in the range.</summary>
   public long NewErrorGroups { get; set; }

   /// <summary>Error groups in the New state.</summary>
   public long OpenErrorGroups { get; set; }

   /// <summary>Access lines in the range.</summary>
   public long Requests { get; set; }

   /// <summary>Audit entries in the range; <c>null</c> without the audit read claim.</summary>
   public long? AuditEntries { get; set; }

   /// <summary>Failed sign-ins in the range; <c>null</c> without the security read claim.</summary>
   public long? FailedSignIns { get; set; }

   /// <summary>Events dropped by the server since start because its queue was full.</summary>
   public long DroppedEvents { get; set; }

   /// <summary>Audit entries still waiting in the application database to be moved to the store.</summary>
   public long AuditOutboxPending { get; set; }

   /// <summary>Disk segments waiting because the store could not be written.</summary>
   public int PendingSegments { get; set; }

   /// <summary>Name of the active write store.</summary>
   public string StoreName { get; set; } = string.Empty;

   /// <summary>Provider of the active write store.</summary>
   public string StoreProvider { get; set; } = string.Empty;

   /// <summary>True when the last write to the store succeeded.</summary>
   public bool StoreHealthy { get; set; }

   /// <summary>Last store error, when unhealthy.</summary>
   public string? StoreMessage { get; set; }
}

/// <summary>Failed sign-ins of one account from one address within a five-minute window.</summary>
public sealed class LogSignInFailureGroup
{
   /// <summary>Start of the five-minute window (UTC).</summary>
   public DateTime WindowStart { get; set; }

   /// <summary>Account that was tried.</summary>
   public string? Account { get; set; }

   /// <summary>Client address.</summary>
   public string? Address { get; set; }

   /// <summary>Number of failures.</summary>
   public int Count { get; set; }
}
