namespace Em.Api.Core.Models;

/// <summary>Where a log store keeps its data.</summary>
public enum LogStoreKind
{
   /// <summary>The application's own core database (connection "Default").</summary>
   Internal = 0,

   /// <summary>Another database reached through its own connection.</summary>
   Connection = 1
}

/// <summary>What a store is used for.</summary>
public enum LogStoreRole
{
   /// <summary>New events are written here. Exactly one store has this role.</summary>
   Write = 0,

   /// <summary>Only read (an older store kept searchable).</summary>
   Read = 1,

   /// <summary>Neither read nor written; kept in the list for reference.</summary>
   Detached = 2
}

/// <summary>Database engine of a log store; the numbers match the engine's <c>DatabaseProvider</c>.</summary>
public enum LogStoreProvider
{
   /// <summary>Microsoft SQL Server.</summary>
   MicrosoftSqlServer = 0,

   /// <summary>MySQL.</summary>
   MySql = 1,

   /// <summary>PostgreSQL.</summary>
   PostgreSql = 2
}

/// <summary>One log store as edited in Log Manager &gt; Settings &gt; Database.</summary>
public sealed class LogStoreDraft
{
   /// <summary>ULID of the store; <c>null</c> for a new store.</summary>
   public string? Id { get; set; }

   /// <summary>Display name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Internal database or another connection.</summary>
   public LogStoreKind Kind { get; set; }

   /// <summary>Role of the store.</summary>
   public LogStoreRole Role { get; set; }

   /// <summary>Provider (connection stores only).</summary>
   public LogStoreProvider Provider { get; set; }

   /// <summary>Server host name.</summary>
   public string? Server { get; set; }

   /// <summary>Port; <c>null</c> = provider default.</summary>
   public int? Port { get; set; }

   /// <summary>Database name.</summary>
   public string? Database { get; set; }

   /// <summary>True for Windows/integrated authentication (SQL Server only).</summary>
   public bool IntegratedSecurity { get; set; }

   /// <summary>Runtime user name.</summary>
   public string? User { get; set; }

   /// <summary>Runtime password; <c>null</c> keeps the stored one. Never returned by the server.</summary>
   public string? Password { get; set; }

   /// <summary>Encrypt the connection.</summary>
   public bool Encrypt { get; set; } = true;

   /// <summary>Trust the server certificate without validation.</summary>
   public bool TrustServerCertificate { get; set; }
}

/// <summary>One log store as the server reports it.</summary>
public sealed class LogStoreInfo
{
   /// <summary>ULID of the store.</summary>
   public string Id { get; set; } = string.Empty;

   /// <summary>Display name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Internal database or another connection.</summary>
   public LogStoreKind Kind { get; set; }

   /// <summary>Role.</summary>
   public LogStoreRole Role { get; set; }

   /// <summary>Provider.</summary>
   public LogStoreProvider Provider { get; set; }

   /// <summary>Server host name.</summary>
   public string? Server { get; set; }

   /// <summary>Port.</summary>
   public int? Port { get; set; }

   /// <summary>Database name.</summary>
   public string? Database { get; set; }

   /// <summary>Integrated authentication.</summary>
   public bool IntegratedSecurity { get; set; }

   /// <summary>Runtime user name.</summary>
   public string? User { get; set; }

   /// <summary>True when a runtime password is stored.</summary>
   public bool HasPassword { get; set; }

   /// <summary>Encrypt the connection.</summary>
   public bool Encrypt { get; set; }

   /// <summary>Trust the server certificate.</summary>
   public bool TrustServerCertificate { get; set; }

   /// <summary>Oldest event time in the store, updated by the retention job.</summary>
   public DateTime? OldestEventUtc { get; set; }

   /// <summary>Newest event time in the store, updated by the retention job.</summary>
   public DateTime? NewestEventUtc { get; set; }
}

/// <summary>The store list: what is saved and what the running server uses.</summary>
public sealed class LogStoreSettingsView
{
   /// <summary>Saved stores.</summary>
   public LogStoreInfo[] Stores { get; set; } = [];

   /// <summary>Stores the running server uses (they differ from <see cref="Stores"/> until restart).</summary>
   public LogStoreInfo[] Active { get; set; } = [];

   /// <summary>True when the saved list differs from the active one (restart required).</summary>
   public bool RestartRequired { get; set; }

   /// <summary>True when <c>EM_LOG_DB_CONNECTION</c> overrides the write store; saving is disabled.</summary>
   public bool EnvironmentOverride { get; set; }

   /// <summary>Revision of the saved list, for optimistic concurrency.</summary>
   public long Revision { get; set; }

   /// <summary>Schema version this engine writes.</summary>
   public int EngineSchemaVersion { get; set; }
}

/// <summary>Request of <c>PostMeta_LogStoreSave</c>.</summary>
public sealed class LogStoreSave
{
   /// <summary>Revision the editor started from.</summary>
   public long Revision { get; set; }

   /// <summary>The full list; exactly one store must have <see cref="LogStoreRole.Write"/>.</summary>
   public LogStoreDraft[] Stores { get; set; } = [];
}

/// <summary>Result of testing a store connection.</summary>
public sealed class LogStoreTestResult
{
   /// <summary>True when the connection opened.</summary>
   public bool Connected { get; set; }

   /// <summary>Schema version found; <c>null</c> when the store tables are missing.</summary>
   public int? SchemaVersion { get; set; }

   /// <summary>Schema version this engine writes.</summary>
   public int EngineSchemaVersion { get; set; }

   /// <summary>True when the store can be used as it is.</summary>
   public bool Ready { get; set; }

   /// <summary>Explanation.</summary>
   public string Message { get; set; } = string.Empty;
}

/// <summary>Request of <c>PostGetMeta_LogStorePrepare</c>. The owner credential is used once and never stored.</summary>
public sealed class LogStorePrepareRequest
{
   /// <summary>Store to prepare (runtime user/password included when rights should be granted).</summary>
   public LogStoreDraft Store { get; set; } = new();

   /// <summary>Owner (DDL) user; empty uses integrated authentication.</summary>
   public string? OwnerUser { get; set; }

   /// <summary>Owner password.</summary>
   public string? OwnerPassword { get; set; }

   /// <summary>Create the database when it does not exist.</summary>
   public bool CreateDatabase { get; set; }

   /// <summary>Grant the runtime user its rights on the store tables.</summary>
   public bool GrantRuntimeUser { get; set; }
}

/// <summary>Outcome of one Prepare step.</summary>
public enum LogPrepareStepStatus
{
   /// <summary>Done.</summary>
   Ok = 0,

   /// <summary>Not needed or not requested.</summary>
   Skipped = 1,

   /// <summary>Failed; later steps did not run.</summary>
   Failed = 2
}

/// <summary>One step of Prepare.</summary>
public sealed class LogPrepareStep
{
   /// <summary>Step name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Outcome.</summary>
   public LogPrepareStepStatus Status { get; set; }

   /// <summary>Explanation.</summary>
   public string Message { get; set; } = string.Empty;
}

/// <summary>Result of Prepare.</summary>
public sealed class LogPrepareResult
{
   /// <summary>True when every step succeeded or was skipped.</summary>
   public bool Success { get; set; }

   /// <summary>Steps in order.</summary>
   public LogPrepareStep[] Steps { get; set; } = [];
}

/// <summary>Retention of one audit group.</summary>
public sealed class LogAuditRetention
{
   /// <summary>Audit group.</summary>
   public string Group { get; set; } = string.Empty;

   /// <summary>Months to keep; <c>null</c> = never delete (module groups only).</summary>
   public int? Months { get; set; }

   /// <summary>Lowest allowed value in months (engine minimum or the module's <c>MinRetention</c>).</summary>
   public int? MinMonths { get; set; }

   /// <summary>True for an engine group (<c>System.*</c>).</summary>
   public bool IsSystem { get; set; }
}

/// <summary>Retention rules of the log store.</summary>
public sealed class LogRetentionRules
{
   /// <summary>Revision, for optimistic concurrency.</summary>
   public long Revision { get; set; }

   /// <summary>Days to keep Trace/Debug/Information diagnostic events.</summary>
   public int InformationDays { get; set; } = 7;

   /// <summary>Days to keep Warning events.</summary>
   public int WarningDays { get; set; } = 30;

   /// <summary>Days to keep Error/Critical events.</summary>
   public int ErrorDays { get; set; } = 90;

   /// <summary>Total size limit of telemetry in GB; <c>null</c> = no limit.</summary>
   public double? TelemetryMaxGb { get; set; } = 20;

   /// <summary>Days to keep access lines.</summary>
   public int AccessDays { get; set; } = 30;

   /// <summary>Days an error group is kept after it was last seen.</summary>
   public int ErrorGroupDays { get; set; } = 180;

   /// <summary>Hour of day (server local time) the daily job runs.</summary>
   public int RunHour { get; set; } = 2;

   /// <summary>Per audit group.</summary>
   public LogAuditRetention[] Audit { get; set; } = [];
}

/// <summary>Rows one retention scope would delete.</summary>
public sealed class LogRetentionPreviewItem
{
   /// <summary>Scope, e.g. <c>Telemetry Information</c> or <c>Audit System.Auth</c>.</summary>
   public string Scope { get; set; } = string.Empty;

   /// <summary>Rows that would be deleted.</summary>
   public long Rows { get; set; }
}

/// <summary>Result of a retention preview.</summary>
public sealed class LogRetentionPreview
{
   /// <summary>Per scope.</summary>
   public LogRetentionPreviewItem[] Items { get; set; } = [];

   /// <summary>Stores that could not be counted.</summary>
   public string[] Warnings { get; set; } = [];
}

/// <summary>Retention rules together with the last run.</summary>
public sealed class LogRetentionView
{
   /// <summary>Current rules.</summary>
   public LogRetentionRules Rules { get; set; } = new();

   /// <summary>When the job last ran (UTC).</summary>
   public DateTime? LastRunUtc { get; set; }

   /// <summary>Summary of the last run.</summary>
   public string? LastRunSummary { get; set; }
}
