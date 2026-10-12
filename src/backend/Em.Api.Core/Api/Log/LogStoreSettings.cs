using System.Data;
using System.Text.Json;
using Em.Api.Core.Models;
using Em.Api.Shared;
using Em.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;

namespace Em.Api.Core.Log;

/// <summary>One stored log store entry (the JSON document in <c>ta_Meta</c> holds a list of these).</summary>
internal sealed class LogStoreEntry
{
   public string Id { get; set; } = string.Empty;
   public string Name { get; set; } = string.Empty;
   public LogStoreKind Kind { get; set; }
   public LogStoreRole Role { get; set; }
   public LogStoreProvider Provider { get; set; }
   public string? Server { get; set; }
   public int? Port { get; set; }
   public string? Database { get; set; }
   public bool IntegratedSecurity { get; set; }
   public string? User { get; set; }
   public byte[]? Password { get; set; }
   public bool Encrypt { get; set; } = true;
   public bool TrustServerCertificate { get; set; }
   public DateTime? OldestEventUtc { get; set; }
   public DateTime? NewestEventUtc { get; set; }

   public LogStoreInfo ToInfo() => new() {
      Id = Id, Name = Name, Kind = Kind, Role = Role, Provider = Provider, Server = Server, Port = Port, Database = Database,
      IntegratedSecurity = IntegratedSecurity, User = User, HasPassword = Password is { Length: > 0 }, Encrypt = Encrypt,
      TrustServerCertificate = TrustServerCertificate, OldestEventUtc = OldestEventUtc, NewestEventUtc = NewestEventUtc
   };
}

/// <summary>The stored store list (<c>Em.Log.Stores</c>).</summary>
internal sealed class LogStoreDocument
{
   public long Revision { get; set; }
   public List<LogStoreEntry> Stores { get; set; } = [];

   /// <summary>The list used when nothing is saved yet: one internal write store.</summary>
   public static LogStoreDocument Default() => new() {
      Stores = [new LogStoreEntry { Id = InternalStoreId, Name = "Internal database", Kind = LogStoreKind.Internal, Role = LogStoreRole.Write }]
   };

   public const string InternalStoreId = "00000000000000000000INTERN";
}

/// <summary>
/// Reads and saves the log store list and its retention rules in <c>ta_Meta</c> of the core database.
/// Runtime passwords are encrypted with AES-GCM; the key is a separate <c>ta_Meta</c> row.
/// </summary>
internal sealed class LogStoreSettings(Func<Em.Api.Core.Models.ApiCoreContext> createContext)
{
   internal const string StoresKey = "Em.Log.Stores";
   internal const string SecretKey = "Em.Log.Key";
   internal const string RetentionKey = "Em.Log.Retention";
   internal const string RetentionLastRunKey = "Em.Log.Retention.LastRun";

   private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

   public async Task<LogStoreDocument> ReadStoresAsync(CancellationToken ct) {
      await using var db = createContext();
      var row = await db.ta_Metas.AsNoTracking().SingleOrDefaultAsync(m => m.cMetaKey == StoresKey, ct);
      return row is null ? LogStoreDocument.Default() : JsonSerializer.Deserialize<LogStoreDocument>(row.cMetaValue, Json) ?? LogStoreDocument.Default();
   }

   /// <summary>Synchronous read for startup, before the host is built.</summary>
   public LogStoreDocument ReadStores() => ReadStoresAsync(CancellationToken.None).GetAwaiter().GetResult();

   public async Task<byte[]> KeyAsync(bool create, CancellationToken ct) {
      await using var db = createContext();
      var row = await db.ta_Metas.AsNoTracking().SingleOrDefaultAsync(m => m.cMetaKey == SecretKey, ct);
      if (row is not null) return Convert.FromBase64String(row.cMetaValue);
      if (!create) throw new InvalidOperationException("The log store secret key is missing.");
      var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
      db.ta_Metas.Add(new ta_Meta { cMetaKey = SecretKey, cMetaValue = Convert.ToBase64String(key), cMetaDescription = "Log store password key", ustamp = DateTime.UtcNow });
      try { await db.SaveChangesAsync(ct); }
      catch (DbUpdateException) {
         db.ChangeTracker.Clear();
         var winner = await db.ta_Metas.AsNoTracking().SingleAsync(m => m.cMetaKey == SecretKey, ct);
         return Convert.FromBase64String(winner.cMetaValue);
      }
      return key;
   }

   public byte[]? Key() {
      try { return KeyAsync(false, CancellationToken.None).GetAwaiter().GetResult(); }
      catch (InvalidOperationException) { return null; }
   }

   /// <summary>Saves the document when its revision still matches; returns the saved document.</summary>
   public async Task<LogStoreDocument> SaveStoresAsync(LogStoreDocument document, long expectedRevision, CancellationToken ct) {
      await using var db = createContext();
      await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
      var row = await db.ta_Metas.AsTracking().SingleOrDefaultAsync(m => m.cMetaKey == StoresKey, ct);
      var current = row is null ? LogStoreDocument.Default() : JsonSerializer.Deserialize<LogStoreDocument>(row.cMetaValue, Json) ?? LogStoreDocument.Default();
      if (current.Revision != expectedRevision)
         throw new ActionException("The log store list was changed by someone else. Reload and try again.", 409);
      document.Revision = current.Revision + 1;
      var value = JsonSerializer.Serialize(document, Json);
      if (row is null) db.ta_Metas.Add(new ta_Meta { cMetaKey = StoresKey, cMetaValue = value, cMetaDescription = "Log stores", ustamp = DateTime.UtcNow });
      else { row.cMetaValue = value; row.ustamp = DateTime.UtcNow; }
      await db.SaveChangesAsync(ct);
      await tx.CommitAsync(ct);
      return document;
   }

   public async Task<string?> ReadValueAsync(string key, CancellationToken ct) {
      await using var db = createContext();
      return (await db.ta_Metas.AsNoTracking().SingleOrDefaultAsync(m => m.cMetaKey == key, ct))?.cMetaValue;
   }

   public async Task WriteValueAsync(string key, string value, string description, CancellationToken ct) {
      await using var db = createContext();
      var row = await db.ta_Metas.AsTracking().SingleOrDefaultAsync(m => m.cMetaKey == key, ct);
      if (row is null) db.ta_Metas.Add(new ta_Meta { cMetaKey = key, cMetaValue = value, cMetaDescription = description, ustamp = DateTime.UtcNow });
      else { row.cMetaValue = value; row.ustamp = DateTime.UtcNow; }
      await db.SaveChangesAsync(ct);
   }

   /// <summary>Builds the provider connection string of a connection store.</summary>
   public static string BuildConnectionString(LogStoreProvider provider, string? server, int? port, string? database,
      bool integrated, string? user, string? password, bool encrypt, bool trustCertificate, bool withoutDatabase = false) {
      if (string.IsNullOrWhiteSpace(server)) throw new ActionException("Server is required.", 400);
      if (!withoutDatabase && string.IsNullOrWhiteSpace(database)) throw new ActionException("Database is required.", 400);
      switch (provider) {
         case LogStoreProvider.MicrosoftSqlServer: {
            var b = new SqlConnectionStringBuilder {
               DataSource = port is { } p ? $"{server},{p}" : server, Encrypt = encrypt, TrustServerCertificate = trustCertificate,
               IntegratedSecurity = integrated, ApplicationName = "Em.Log"
            };
            if (!withoutDatabase) b.InitialCatalog = database;
            if (!integrated) { b.UserID = user ?? ""; b.Password = password ?? ""; }
            return b.ConnectionString;
         }
         case LogStoreProvider.MySql: {
            var b = new MySqlConnectionStringBuilder { Server = server, UserID = user ?? "", Password = password ?? "",
               SslMode = encrypt ? (trustCertificate ? MySqlSslMode.Required : MySqlSslMode.VerifyFull) : MySqlSslMode.None };
            if (port is { } p) b.Port = (uint)p;
            if (!withoutDatabase) b.Database = database;
            return b.ConnectionString;
         }
         case LogStoreProvider.PostgreSql: {
            var b = new NpgsqlConnectionStringBuilder { Host = server, Username = user, Password = password,
               SslMode = encrypt ? (trustCertificate ? SslMode.Require : SslMode.VerifyFull) : SslMode.Disable };
            if (port is { } p) b.Port = p;
            b.Database = withoutDatabase ? "postgres" : database;
            return b.ConnectionString;
         }
         default: throw new ActionException($"Provider '{provider}' is not supported.", 400);
      }
   }
}
