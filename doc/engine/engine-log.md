# Log engine

One log engine for a single installation: **data audit** of modules (who changed which document, field by
field), **engine audit** (`System.*`: sign-in, passwords, users, roles, robots, settings, deletions in the
registry, NuGet and object storage) and **telemetry** (diagnostic lines, errors grouped by fingerprint, request
access lines, logs of WPF/MAUI clients). Everything is read in the WPF **Log Manager** and, per document, through
`IAuditServices` / `IAuditReader`.

## Enabling it on a host

```csharp
var builder = EmAppBuilder.CreateBuilder(args);
builder.AddLogServer();          // telemetry + audit + Log Manager actions
// modules register their audit groups (see below)
```

The sample host `Em.Api` calls `AddLogServer()` unless `"modules": { "logServer": false }` or
`EM_MODULE_LOGSERVER=false` is set. Without `AddLogServer()` audit entries are discarded (a `NullAuditWriter`)
and telemetry only goes to the console.

### Database

Run on the core database, after the other `tables/` scripts:

| Script | Creates |
| --- | --- |
| `doc/sqlscript/mssql/tables/070-log-audit.sql` | `ta_LogAudit` – the audit outbox in the application database (written in the data transaction) |
| `doc/sqlscript/mssql/tables/071-log-store.sql` | Store tables `ta_LogEvent`, `ta_LogError`, `ta_LogIngestBatch`, `ta_LogSchema` and the store's own `ta_LogAudit` |

The same scripts exist for MySQL and PostgreSQL in `doc/sqlscript/mysql/tables/` and
`doc/sqlscript/postgresql/tables/`. Startup fails when the schema is missing or older than the engine
(`ta_LogSchema`, schema version 1). A separate store database does not need to be scripted by hand: Log
Manager's **Prepare** runs the embedded `071` script for its provider.

`ta_LogAudit` is mapped in every `EmDbContext` (the base `OnModelCreating` configures it), so a module database
that writes audit entries also needs `070-log-audit.sql`.

### Configuration

`emapi-config.json`:

```jsonc
"storage": { "logPath": "./data/logs" },   // disk segments when the store cannot be written
"log": {
  "storeRequestStartLines": false,          // IN/START request lines are console only by default
  "queueCapacity": 20000,                   // in-memory queue; events above it are counted as dropped
  "outboxMaxMb": 500                        // warning threshold of unsent audit outbox rows
}
```

`appsettings.json` has a `Logging:EmLog` section: the levels that reach the store, independent of the console
levels. Request trace lines of the log module itself (`core.log`) are written at Debug so reading logs does not
create log rows about itself; 5xx stays an error.

## Claims

Module `core.log`:

| Claim | Allows |
| --- | --- |
| `Log Manager Access` | Opening Log Manager (`admin.logmanager`) and the summary |
| `Log Telemetry Read` | Log and Errors tabs, error triage (`PostMeta_LogErrorState`) |
| `Log Audit Read` | Audit tab: every audit group |
| `Log Security Read` | Security tab: `System.Auth`, `System.Password` and failed sign-ins (also without `Log Audit Read`) |
| `Log Manager Settings` | Settings > Database (stores) and Settings > Retention |

Module screens read audit of their own documents through `IAuditServices` (module `core.audit`); there the
group's own read rule (`CanRead` / `CanReadClaim`) decides, not the Log Manager claims.

## Writing module audit

### 1. Register the group

```csharp
builder.AddAudit("Invoice", a => a
   .Title("Invoices")
   .Entry("created", "Created invoice")
   .Entry("updated", "Updated invoice")
   .Entry("voided", "Voided invoice", requireReason: true)
   .Entry("printed", "Printed invoice", fromClient: true)   // may be reported by clients (PostMeta_AuditEntry)
   .CanReadClaim("Sales:Invoice Read")
   .MinRetention(TimeSpan.FromDays(365 * 5))
   .Database("Sales"));                                      // extra connection: startup checks its ta_LogAudit, the shipper moves it
builder.AddScoped<InvoiceAudit>();
```

`System.` is reserved for the engine. Unknown groups or kinds and a missing required reason throw at
`AddEntry`, before any data is saved.

### 2. Wrap `IAuditWriter` in a module class

The reference example is `src/modules/Em.Test/Em.Test.Api/TestAudit.cs`:

```csharp
public sealed class InvoiceAudit(IAuditWriter audit)
{
   public InvoiceAudit Use(DbContext context) => new(audit.Use(context));

   public void Updated(ta_Invoice? before, ta_Invoice after) {
      var changes = AuditDiff.Of(before, after);
      if (changes.Length == 0) return;
      audit.AddEntry("Invoice", after.cInvoiceId, "updated", docNo: after.cInvoiceNo,
         summary: $"Updated invoice {after.cInvoiceNo}", changes: changes);
   }
}
```

### 3. Rules

- `AddEntry` only **parks** the entry. The next `SaveChanges` of an engine context in the same request adds a
  `ta_LogAudit` row to that context, so the entry commits or rolls back with the data. Call it **before**
  `SaveChanges`.
- When a request saves through more than one context, direct entries with `Use(context)`; parking entries
  without `Use` and then saving a second context throws instead of guessing.
- An entry still parked when a successful request ends is reported as an error ("audit entry not saved").
- `WriteNowAsync` writes in its own transaction – for events that change no data or belong to a failing request
  (a refused sign-in).
- `SaveAsync(context)` directs every parked entry to a context and saves it – for bulk paths that save with
  `ExecuteUpdate`/`ExecuteDelete` inside an explicit transaction.
- `AuditDiff.Of(before, after, ignore, secret)` compares scalar properties (`ustamp`, `datestamp` and
  `json_object` are ignored by default); properties named in `secret` record only `(set)`/`(changed)`/`(cleared)`,
  never the value. Composite keys are written as an ordered JSON array (`AuditKey`).
- Actor, session, request id, client address and application are filled from the request; debug tokens are
  recorded as actor type `debug` with `ActingAs`.

### Reading

- Server: inject `IAuditReader` (`ByDocAsync`, `ByDocNoAsync`, `ByTypeAsync`, `SinceAsync`, `QueryAsync`).
- Clients: `IAuditServices` / `AuditClientService` (`GetMeta_AuditActivity_ByDoc`, `_ByDocNo`, `_ByType`,
  `_ByTarget`, `_Since`).

Pages are newest first with an opaque `NextCursor`; pass it back unchanged. The reader merges the active store,
read stores and outboxes not yet shipped; a source that cannot be read is listed in `Warnings` and the page is
still valid. User Editor's **Activity** tab is built on `_ByTarget` (user target of `System.User`,
`System.Password` and `System.Auth`).

## Telemetry

- Server: an `ILoggerProvider` (`EmLog`) puts every event on a bounded queue; a background writer stores it.
  When the store cannot be written, batches go to disk segments under `storage.logPath` and are resent later.
- Errors carry a fingerprint (exception type + top application frame) and are grouped in `ta_LogError` with
  triage states New / Acknowledged / Resolved / Ignored; a resolved group seen again becomes New.
- Every request line (`DONE`, `ABORT`, `REJECT`) carries a unique request id (`instance:id:start`), the same id
  audit entries of that request carry.
- Clients (WPF and MAUI): `ClientLogProvider` writes to `%LocalAppData%\<ApplicationName>\Logs\outbox`
  (200 MB limit, oldest segments dropped and counted). `ClientLogShipper` sends batches with
  `PostMeta_LogIngest` while signed in and before sign-out; batch ids make resends harmless. Events keep the user
  of the moment they were written. Unhandled exceptions of the client are logged as errors.

## Log stores (Log Manager > Settings > Database)

The default store is the core database (**Internal**). Another database (SQL Server, MySQL or PostgreSQL) can be
added:

1. **Test** – connects with the runtime credential and reports whether the store is ready.
2. **Prepare** – with an owner credential used once and never stored: creates the database (optional), runs the
   schema script, upgrades it, grants the runtime user read/write without DDL (optional) and verifies with a test
   event.
3. **Save** – refused while the write store is not ready. Exactly one store has the **Write** role; old stores can
   stay **Read** (still searched) or **Detached**. Changes take effect after an API restart.

The list lives in `ta_Meta` (`Em.Log.Stores`); runtime passwords are AES-GCM encrypted with a key in its own
`ta_Meta` row and are never returned by the server. Audit outbox rows move from the application database to a
separate store through the shipper; while it is the same database they are already in place.

`EM_LOG_DB_CONNECTION` (with `EM_LOG_DB_PROVIDER` = `MicrosoftSqlServer` | `MySql` | `PostgreSql`, default SQL
Server) overrides the write store; saving is then disabled in the UI.

## Retention (Log Manager > Settings > Retention)

| Rule | Default |
| --- | --- |
| Trace/Debug/Information events | 7 days |
| Warning events | 30 days |
| Error/Critical events | 90 days |
| Access lines | 30 days |
| Telemetry size limit | 20 GB (oldest deleted first) |
| Error groups after last seen | 180 days |
| Audit, `System.*` groups | 24 months (minimum 12) |
| Audit, module groups | Never deleted unless set; never below the group's `MinRetention` |

The job runs daily at `RunHour` (server local time, default 02:00). **Preview** counts what a rule set would
delete before saving. Changing the rules and every run are audited in `System.Settings`.

## Limitations

- One API instance per log store: the shipper and the retention job assume a single writer.
- Telemetry queries are limited to 31 days per request.
- The WPF Log Manager and the User Editor Activity tab are compiled and covered by view-model tests but still
  need checking on Windows; the MAUI hooks have not been built in CI.

## Maintainer notes

An HTTP smoke harness (sign-in audit, module audit, client ingest, failed sign-in grouping, stores Prepare on
MySQL, PostgreSQL write store with disk fallback) is kept outside the repository under
`../.artefacts/em-system/scripts/log-smoke/`.
