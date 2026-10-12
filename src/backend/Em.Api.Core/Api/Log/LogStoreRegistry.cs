using Em.Api.Core.Models;
using Em.Api.Shared;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log;

/// <summary>A store the running server uses, with its resolved connection.</summary>
internal sealed record LogStoreHandle(string Id, string Name, LogStoreKind Kind, LogStoreRole Role, DatabaseProvider Provider,
   string ConnectionString)
{
   /// <summary>Creates a context on this store; the caller disposes it.</summary>
   public LogStoreDbContext CreateContext() {
      var options = new DbContextOptionsBuilder<LogStoreDbContext>();
      options.UseEmProvider(Provider, ConnectionString);
      return new LogStoreDbContext(options.Options);
   }
}

/// <summary>
/// The stores fixed at startup: one write store and zero or more read stores. Changes saved from the Log
/// Manager apply after a restart (managed-settings pattern).
/// </summary>
internal sealed class LogStoreRegistry
{
   public LogStoreRegistry(LogStoreHandle write, IReadOnlyList<LogStoreHandle> read, bool environmentOverride,
      DbConnectionInfo core, IReadOnlyList<DbConnectionInfo> auditSources, IReadOnlyList<LogStoreEntry> activeEntries) {
      Write = write;
      Read = read;
      EnvironmentOverride = environmentOverride;
      Core = core;
      AuditSources = auditSources;
      ActiveEntries = activeEntries;
   }

   /// <summary>Where new events and moved audit entries are written.</summary>
   public LogStoreHandle Write { get; }

   /// <summary>Older stores that are only read.</summary>
   public IReadOnlyList<LogStoreHandle> Read { get; }

   /// <summary>Write store first, then the read stores.</summary>
   public IEnumerable<LogStoreHandle> All => Read.Prepend(Write);

   /// <summary>True when <c>EM_LOG_DB_CONNECTION</c> replaced the write store.</summary>
   public bool EnvironmentOverride { get; }

   /// <summary>The core database (outbox of engine audit entries).</summary>
   public DbConnectionInfo Core { get; }

   /// <summary>Every database that may hold audit outbox rows (core first).</summary>
   public IReadOnlyList<DbConnectionInfo> AuditSources { get; }

   /// <summary>The store entries that were active at startup, as saved (for the settings screen).</summary>
   public IReadOnlyList<LogStoreEntry> ActiveEntries { get; }

   /// <summary>True when audit entries written to <paramref name="connectionString"/> are already in the write store.</summary>
   public bool IsStoreDatabase(string? connectionString) => SameDatabase(connectionString, Write.ConnectionString);

   /// <summary>
   /// True when two connection strings point to the same server, port and database, whatever their spelling
   /// (<c>Server</c>/<c>Data Source</c>/<c>Host</c>, <c>Database</c>/<c>Initial Catalog</c>, credentials and options).
   /// </summary>
   internal static bool SameDatabase(string? a, string? b) {
      if (a is null || b is null) return false;
      return Target(a) is { } x && Target(b) is { } y && x == y;
   }

   private static (string Server, string Database)? Target(string connectionString) {
      try {
         var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
         string? Read(params string[] keys) {
            foreach (var key in keys)
               if (builder.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text) return text.Trim();
            return null;
         }
         var server = (Read("server", "data source", "host", "address", "addr", "network address") ?? "").ToLowerInvariant();
         if (Read("port") is { } port && !server.Contains(',')) server += "," + port;
         if (server.StartsWith("tcp:", StringComparison.Ordinal)) server = server[4..];
         var database = (Read("database", "initial catalog") ?? "").ToLowerInvariant();
         return (server, database);
      }
      catch (ArgumentException) {
         return null;
      }
   }

   /// <summary>
   /// Resolves the saved list (or the environment override) into handles. Connection stores whose password
   /// cannot be decrypted are skipped with a warning; when the write store cannot be resolved the internal
   /// database is used so telemetry is never lost silently.
   /// </summary>
   public static LogStoreRegistry Resolve(LogStoreDocument document, byte[]? key, DbConnectionInfo core,
      IReadOnlyList<DbConnectionInfo> auditSources, Action<string> warn) {
      var internalHandle = new LogStoreHandle(LogStoreDocument.InternalStoreId, "Internal database", LogStoreKind.Internal,
         LogStoreRole.Write, core.Provider, core.ConnectionString);

      var envConnection = Environment.GetEnvironmentVariable("EM_LOG_DB_CONNECTION");
      LogStoreHandle? write = null;
      var envOverride = false;
      if (!string.IsNullOrWhiteSpace(envConnection)) {
         var providerName = Environment.GetEnvironmentVariable("EM_LOG_DB_PROVIDER");
         var provider = DatabaseProvider.MicrosoftSqlServer;
         if (!string.IsNullOrWhiteSpace(providerName) && !Enum.TryParse(providerName, true, out provider))
            throw new InvalidOperationException("EM_LOG_DB_PROVIDER must be MicrosoftSqlServer, MySql, or PostgreSql.");
         write = new LogStoreHandle("ENVIRONMENT", "EM_LOG_DB_CONNECTION", LogStoreKind.Connection, LogStoreRole.Write, provider, envConnection);
         envOverride = true;
      }

      var read = new List<LogStoreHandle>();
      foreach (var entry in document.Stores) {
         if (entry.Role == LogStoreRole.Detached) continue;
         LogStoreHandle handle;
         if (entry.Kind == LogStoreKind.Internal) {
            handle = internalHandle with { Id = entry.Id, Name = entry.Name, Role = entry.Role };
         }
         else {
            try {
               var password = entry.Password is { Length: > 0 } && key is not null ? LogSecrets.Decrypt(key, entry.Password) : null;
               var cs = LogStoreSettings.BuildConnectionString(entry.Provider, entry.Server, entry.Port, entry.Database,
                  entry.IntegratedSecurity, entry.User, password, entry.Encrypt, entry.TrustServerCertificate);
               handle = new LogStoreHandle(entry.Id, entry.Name, LogStoreKind.Connection, entry.Role, (DatabaseProvider)(int)entry.Provider, cs);
            }
            catch (Exception ex) {
               warn($"Log store '{entry.Name}' cannot be used: {ex.Message}");
               continue;
            }
         }

         if (entry.Role == LogStoreRole.Write) {
            if (envOverride) read.Add(handle with { Role = LogStoreRole.Read });
            else write ??= handle;
         }
         else read.Add(handle);
      }

      if (write is null) {
         warn("No usable log write store is configured; the internal database is used.");
         write = internalHandle;
      }

      read.RemoveAll(r => SameDatabase(r.ConnectionString, write.ConnectionString));
      return new LogStoreRegistry(write, read, envOverride, core, auditSources, document.Stores);
   }
}
