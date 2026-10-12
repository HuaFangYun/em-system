using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Em.Api.Core.Models;
using Em.Api.Shared;
using Em.Shared;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace Em.Api.Core.Log;

/// <summary>
/// Prepares a log store with an owner (DDL) credential that is used once and never stored: create the database
/// when asked, run the store script of the provider (never <c>DROP</c>), refuse a newer schema, grant the runtime
/// user and verify with a write, read and delete. Every step is reported.
/// </summary>
internal static partial class LogStorePrepare
{
   [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,128}$")]
   private static partial Regex SafeName();

   [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
   private static partial Regex GoSeparator();

   /// <summary>The embedded store script of a provider (the same file as in <c>doc/sqlscript</c>).</summary>
   internal static string Script(LogStoreProvider provider) {
      var name = provider switch {
         LogStoreProvider.MicrosoftSqlServer => "Em.Log.Store.mssql.sql",
         LogStoreProvider.MySql => "Em.Log.Store.mysql.sql",
         LogStoreProvider.PostgreSql => "Em.Log.Store.postgresql.sql",
         _ => throw new ActionException("Unknown provider.", 400)
      };
      using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
         ?? throw new InvalidOperationException($"Embedded script '{name}' is missing.");
      using var reader = new StreamReader(stream);
      return reader.ReadToEnd();
   }

   public static async Task<LogPrepareResult> RunAsync(LogStorePrepareRequest request, string? runtimePassword, CancellationToken ct) {
      var steps = new List<LogPrepareStep>();
      var store = request.Store;
      var result = new LogPrepareResult { Success = false };
      bool Step(string name, LogPrepareStepStatus status, string message) {
         steps.Add(new LogPrepareStep { Name = name, Status = status, Message = message });
         return status != LogPrepareStepStatus.Failed;
      }

      try {
         if (store.Kind != LogStoreKind.Connection) throw new ActionException("Only a connection store can be prepared; the internal database uses the scripts in doc/sqlscript.", 400);
         if (string.IsNullOrWhiteSpace(store.Database) || !SafeName().IsMatch(store.Database))
            throw new ActionException("The database name may only contain letters, digits, '_', '-' and '.'.", 400);
         if (request.GrantRuntimeUser && (string.IsNullOrWhiteSpace(store.User) || !SafeName().IsMatch(store.User)))
            throw new ActionException("The runtime user name may only contain letters, digits, '_', '-' and '.'.", 400);

         var integratedOwner = string.IsNullOrWhiteSpace(request.OwnerUser);
         var ownerServer = LogStoreSettings.BuildConnectionString(store.Provider, store.Server, store.Port, store.Database,
            integratedOwner && store.Provider == LogStoreProvider.MicrosoftSqlServer, request.OwnerUser, request.OwnerPassword, store.Encrypt,
            store.TrustServerCertificate, withoutDatabase: true);

         // 1. Connect with the owner credential.
         await using (var conn = Open(store.Provider, ownerServer)) {
            await conn.OpenAsync(ct);
            Step("Connect", LogPrepareStepStatus.Ok, $"Connected to {store.Server} as {(integratedOwner ? "the integrated account" : request.OwnerUser)}.");

            // 2. Create the database when asked and missing.
            if (request.CreateDatabase) {
               var exists = await DatabaseExistsAsync(conn, store.Provider, store.Database, ct);
               if (exists) Step("Create database", LogPrepareStepStatus.Skipped, $"Database '{store.Database}' already exists.");
               else {
                  await ExecAsync(conn, store.Provider switch {
                     LogStoreProvider.MicrosoftSqlServer => $"CREATE DATABASE [{store.Database}]",
                     LogStoreProvider.MySql => $"CREATE DATABASE `{store.Database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci",
                     _ => $"CREATE DATABASE \"{store.Database}\" ENCODING 'UTF8'"
                  }, ct);
                  Step("Create database", LogPrepareStepStatus.Ok, $"Database '{store.Database}' created.");
               }
            }
            else Step("Create database", LogPrepareStepStatus.Skipped, "Not requested.");
         }

         var ownerDb = LogStoreSettings.BuildConnectionString(store.Provider, store.Server, store.Port, store.Database,
            integratedOwner && store.Provider == LogStoreProvider.MicrosoftSqlServer, request.OwnerUser, request.OwnerPassword, store.Encrypt,
            store.TrustServerCertificate);
         await using (var conn = Open(store.Provider, ownerDb)) {
            await conn.OpenAsync(ct);

            // 3/4. Schema: refuse a newer one, otherwise run the idempotent script (creates what is missing).
            var version = await SchemaVersionAsync(conn, ct);
            if (version > LogStoreDbContext.SchemaVersion) {
               Step("Schema", LogPrepareStepStatus.Failed, $"The store has schema version {version}, newer than this engine ({LogStoreDbContext.SchemaVersion}). Update the API first.");
               result.Steps = [.. steps];
               return result;
            }
            var script = Script(store.Provider);
            var batches = store.Provider == LogStoreProvider.MicrosoftSqlServer ? GoSeparator().Split(script) : [script];
            foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b))) await ExecAsync(conn, batch, ct);
            Step("Schema", LogPrepareStepStatus.Ok, version is null
               ? $"Store tables created (schema version {LogStoreDbContext.SchemaVersion})."
               : $"Store tables checked (schema version {LogStoreDbContext.SchemaVersion}).");
            Step("Upgrade", LogPrepareStepStatus.Skipped, "No upgrade needed.");

            // 5. Runtime rights.
            if (request.GrantRuntimeUser) {
               foreach (var sql in GrantStatements(store.Provider, store.Database!, store.User!)) await ExecAsync(conn, sql, ct);
               Step("Grant runtime user", LogPrepareStepStatus.Ok, $"'{store.User}' may read and write the store tables (no DDL).");
            }
            else Step("Grant runtime user", LogPrepareStepStatus.Skipped, "Not requested.");
         }

         // 6. Verify with the runtime credential: write, read and delete one test event.
         var runtime = LogStoreSettings.BuildConnectionString(store.Provider, store.Server, store.Port, store.Database, store.IntegratedSecurity,
            store.User, runtimePassword, store.Encrypt, store.TrustServerCertificate);
         await VerifyAsync(new LogStoreHandle("verify", store.Name, LogStoreKind.Connection, LogStoreRole.Write, (DatabaseProvider)(int)store.Provider, runtime), ct);
         Step("Verify", LogPrepareStepStatus.Ok, "A test event was written, read and deleted with the runtime credential.");
         result.Success = true;
      }
      catch (ActionException) { throw; }
      catch (Exception ex) when (ex is not OperationCanceledException) {
         Step(steps.Count == 0 ? "Connect" : "Error", LogPrepareStepStatus.Failed, ex.GetBaseException().Message);
      }
      result.Steps = [.. steps];
      return result;
   }

   /// <summary>Writes, reads and deletes one test event.</summary>
   internal static async Task VerifyAsync(LogStoreHandle store, CancellationToken ct) {
      using var _ = LogGuard.Suppress();
      await using var db = store.CreateContext();
      var marker = "Em.Log.Verify " + Guid.NewGuid().ToString("N");
      var row = new ta_LogEvent {
         cLogEventTime = DateTime.UtcNow, cLogEventKind = 0, cLogEventLevel = (int)LogEventLevel.Information, cLogEventCategory = "Em.Log.Verify",
         cLogEventMessage = marker, cLogEventSource = "server", cLogEventReceived = DateTime.UtcNow
      };
      db.Events.Add(row);
      await db.SaveChangesAsync(ct);
      var found = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Events, e => e.cLogEventMessage == marker, ct);
      if (found != 1) throw new InvalidOperationException("The test event could not be read back.");
      db.Events.Remove(row);
      await db.SaveChangesAsync(ct);
   }

   private static DbConnection Open(LogStoreProvider provider, string connectionString) => provider switch {
      LogStoreProvider.MicrosoftSqlServer => new SqlConnection(connectionString),
      LogStoreProvider.MySql => new MySqlConnection(connectionString),
      _ => new NpgsqlConnection(connectionString)
   };

   private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      cmd.CommandTimeout = 120;
      await cmd.ExecuteNonQueryAsync(ct);
   }

   private static async Task<bool> DatabaseExistsAsync(DbConnection conn, LogStoreProvider provider, string database, CancellationToken ct) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = provider switch {
         LogStoreProvider.MicrosoftSqlServer => "SELECT COUNT(*) FROM sys.databases WHERE name = @n",
         LogStoreProvider.MySql => "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @n",
         _ => "SELECT COUNT(*) FROM pg_database WHERE datname = @n"
      };
      var p = cmd.CreateParameter();
      p.ParameterName = "@n";
      p.Value = database;
      cmd.Parameters.Add(p);
      return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
   }

   private static async Task<int?> SchemaVersionAsync(DbConnection conn, CancellationToken ct) {
      try {
         await using var cmd = conn.CreateCommand();
         cmd.CommandText = "SELECT cLogSchemaVersion FROM ta_LogSchema WHERE cLogSchemaId = 1";
         var value = await cmd.ExecuteScalarAsync(ct);
         return value is null or DBNull ? null : Convert.ToInt32(value);
      }
      catch (DbException) {
         return null; // table not there yet
      }
   }

   internal static IEnumerable<string> GrantStatements(LogStoreProvider provider, string database, string user) {
      string[] readWriteDelete = ["ta_LogEvent", "ta_LogIngestBatch", "ta_LogAudit", "ta_LogSchema"];
      switch (provider) {
         case LogStoreProvider.MicrosoftSqlServer:
            yield return $"IF USER_ID(N'{user}') IS NULL CREATE USER [{user}] FOR LOGIN [{user}]";
            yield return $"GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].[ta_LogError] TO [{user}]";
            foreach (var t in readWriteDelete) yield return $"GRANT SELECT, INSERT, DELETE ON [dbo].[{t}] TO [{user}]";
            break;
         case LogStoreProvider.MySql:
            yield return $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{database}`.`ta_LogError` TO '{user}'@'%'";
            foreach (var t in readWriteDelete) yield return $"GRANT SELECT, INSERT, DELETE ON `{database}`.`{t}` TO '{user}'@'%'";
            break;
         default:
            yield return $"GRANT SELECT, INSERT, UPDATE, DELETE ON \"ta_LogError\" TO \"{user}\"";
            foreach (var t in readWriteDelete) yield return $"GRANT SELECT, INSERT, DELETE ON \"{t}\" TO \"{user}\"";
            yield return $"GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO \"{user}\"";
            break;
      }
   }
}
