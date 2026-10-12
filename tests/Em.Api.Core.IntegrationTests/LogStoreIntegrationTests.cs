using System.Data.Common;
using Em.Api.Core.Log;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Api.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;

namespace Em.Api.Core.IntegrationTests;

/// <summary>
/// One database server the log store tests run against. SQL Server comes from <see cref="SqlServerDatabase"/>
/// (Windows Authentication); PostgreSQL and MySQL are opt-in through <c>EM_TEST_PG</c> and <c>EM_TEST_MYSQL</c>
/// (an admin connection string without a database, e.g. <c>Host=localhost;Username=postgres;Password=...</c>).
/// A server that is not configured or not reachable skips the test.
/// </summary>
internal sealed class LogTarget(LogStoreProvider provider, string server, int? port, bool integrated, string? user, string? password)
{
   public LogStoreProvider Provider { get; } = provider;

   public static LogTarget For(string name, SqlServerDatabase sql) {
      switch (name) {
         case "mssql": {
            var b = new SqlConnectionStringBuilder(sql.ConnectionString);
            return new LogTarget(LogStoreProvider.MicrosoftSqlServer, b.DataSource, null, b.IntegratedSecurity,
               b.IntegratedSecurity ? null : b.UserID, b.IntegratedSecurity ? null : b.Password);
         }
         case "postgresql": {
            var cs = Environment.GetEnvironmentVariable("EM_TEST_PG");
            if (string.IsNullOrWhiteSpace(cs)) Assert.Skip("Set EM_TEST_PG to run the PostgreSQL log store tests.");
            var b = new NpgsqlConnectionStringBuilder(cs);
            return new LogTarget(LogStoreProvider.PostgreSql, b.Host!, b.Port, false, b.Username, b.Password);
         }
         default: {
            var cs = Environment.GetEnvironmentVariable("EM_TEST_MYSQL");
            if (string.IsNullOrWhiteSpace(cs)) Assert.Skip("Set EM_TEST_MYSQL to run the MySQL log store tests.");
            var b = new MySqlConnectionStringBuilder(cs);
            return new LogTarget(LogStoreProvider.MySql, b.Server, (int)b.Port, false, b.UserID, b.Password);
         }
      }
   }

   public DatabaseProvider Db => (DatabaseProvider)(int)Provider;

   public string ConnectionString(string database) =>
      LogStoreSettings.BuildConnectionString(Provider, server, port, database, integrated, user, password, encrypt: Provider == LogStoreProvider.MicrosoftSqlServer, trustCertificate: true);

   public DbConnectionInfo Info(string database) => new(database, ConnectionString(database), Db);

   public LogStoreDraft Draft(string database, LogStoreRole role = LogStoreRole.Write) => new() {
      Name = database, Kind = LogStoreKind.Connection, Role = role, Provider = Provider, Server = server, Port = port, Database = database,
      IntegratedSecurity = integrated, User = user, Password = password, Encrypt = Provider == LogStoreProvider.MicrosoftSqlServer, TrustServerCertificate = true
   };

   public LogStoreHandle Handle(string database, LogStoreRole role = LogStoreRole.Write) =>
      new(database, database, LogStoreKind.Connection, role, Db, ConnectionString(database));

   /// <summary>Creates and prepares a store database through Prepare itself.</summary>
   public async Task<string> PrepareStoreAsync(List<string> created, CancellationToken ct) {
      var name = NewName();
      created.Add(name);
      var result = await LogStorePrepare.RunAsync(new LogStorePrepareRequest {
         Store = Draft(name), OwnerUser = user, OwnerPassword = password, CreateDatabase = true, GrantRuntimeUser = false
      }, password, ct);
      Assert.True(result.Success, string.Join("; ", result.Steps.Select(s => $"{s.Name} {s.Status} {s.Message}")));
      return name;
   }

   /// <summary>Creates an empty application database with ta_Meta, ta_LogAudit (070 script) and an Invoices table.</summary>
   public async Task<string> CreateAppDatabaseAsync(List<string> created, CancellationToken ct) {
      var name = NewName();
      created.Add(name);
      await ExecAsync(null, Provider switch {
         LogStoreProvider.MicrosoftSqlServer => $"CREATE DATABASE [{name}]",
         LogStoreProvider.MySql => $"CREATE DATABASE `{name}`",
         _ => $"CREATE DATABASE \"{name}\""
      }, ct);
      var folder = Provider switch { LogStoreProvider.MicrosoftSqlServer => "mssql", LogStoreProvider.MySql => "mysql", _ => "postgresql" };
      var script = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "doc", "sqlscript", folder, "tables", "070-log-audit.sql"), ct);
      foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
         if (!string.IsNullOrWhiteSpace(batch)) await ExecAsync(name, batch, ct);
      await ExecAsync(name, Provider switch {
         LogStoreProvider.MicrosoftSqlServer => """
            CREATE TABLE ta_Meta(cMetaKey varchar(50) NOT NULL PRIMARY KEY, cMetaValue nvarchar(max) NOT NULL, cMetaDescription varchar(500) NULL, ustamp datetime NOT NULL);
            CREATE TABLE Invoices(cInvoiceId varchar(26) NOT NULL PRIMARY KEY, cInvoiceNo varchar(50) NOT NULL, cInvoiceTotal decimal(18,2) NOT NULL, ustamp datetime2 NOT NULL);
            """,
         LogStoreProvider.MySql => """
            CREATE TABLE ta_Meta(cMetaKey varchar(50) NOT NULL PRIMARY KEY, cMetaValue longtext NOT NULL, cMetaDescription varchar(500) NULL, ustamp datetime(6) NOT NULL);
            CREATE TABLE Invoices(cInvoiceId varchar(26) NOT NULL PRIMARY KEY, cInvoiceNo varchar(50) NOT NULL, cInvoiceTotal decimal(18,2) NOT NULL, ustamp datetime(6) NOT NULL);
            """,
         _ => """
            CREATE TABLE "ta_Meta"("cMetaKey" varchar(50) NOT NULL PRIMARY KEY, "cMetaValue" text NOT NULL, "cMetaDescription" varchar(500) NULL, "ustamp" timestamptz NOT NULL);
            CREATE TABLE "Invoices"("cInvoiceId" varchar(26) NOT NULL PRIMARY KEY, "cInvoiceNo" varchar(50) NOT NULL, "cInvoiceTotal" numeric(18,2) NOT NULL, "ustamp" timestamptz NOT NULL);
            """
      }, ct);
      return name;
   }

   public async Task ExecAsync(string? database, string sql, CancellationToken ct) {
      await using var connection = Open(database);
      await connection.OpenAsync(ct);
      await using var command = connection.CreateCommand();
      command.CommandText = sql;
      await command.ExecuteNonQueryAsync(ct);
   }

   public async Task<long> CountAsync(string database, string table, string where, CancellationToken ct) {
      await using var connection = Open(database);
      await connection.OpenAsync(ct);
      await using var command = connection.CreateCommand();
      var q = Provider == LogStoreProvider.PostgreSql ? $"\"{table}\"" : table;
      command.CommandText = $"SELECT COUNT(*) FROM {q} WHERE {where}";
      return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
   }

   public string Col(string name) => Provider == LogStoreProvider.PostgreSql ? $"\"{name}\"" : name;

   private DbConnection Open(string? database) {
      var cs = database is null
         ? LogStoreSettings.BuildConnectionString(Provider, server, port, null, integrated, user, password, Provider == LogStoreProvider.MicrosoftSqlServer, true, withoutDatabase: true)
         : ConnectionString(database);
      return Provider switch {
         LogStoreProvider.MicrosoftSqlServer => new SqlConnection(cs),
         LogStoreProvider.MySql => new MySqlConnection(cs),
         _ => new NpgsqlConnection(cs)
      };
   }

   public async Task DropAsync(IEnumerable<string> databases) {
      SqlConnection.ClearAllPools();
      NpgsqlConnection.ClearAllPools();
      MySqlConnection.ClearAllPools();
      foreach (var name in databases) {
         try {
            await ExecAsync(null, Provider switch {
               LogStoreProvider.MicrosoftSqlServer => $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END",
               LogStoreProvider.MySql => $"DROP DATABASE IF EXISTS `{name}`",
               _ => $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)"
            }, CancellationToken.None);
         }
         catch (DbException) { }
      }
   }

   private static string NewName() => "emlogtest_" + Guid.NewGuid().ToString("N")[..12];

   private static string RepoRoot() {
      for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
         if (File.Exists(Path.Combine(dir.FullName, "doc", "sqlscript", "mssql", "tables", "071-log-store.sql"))) return dir.FullName;
      throw new InvalidOperationException("Repository root with doc/sqlscript was not found.");
   }
}

/// <summary>A module context with one table, as a module would register it.</summary>
public sealed class LogTestInvoiceContext(DbContextOptions<LogTestInvoiceContext> options) : EmDbContext(options)
{
   public DbSet<LogTestInvoice> Invoices => Set<LogTestInvoice>();
}

public sealed class LogTestInvoice
{
   [System.ComponentModel.DataAnnotations.Key] public string cInvoiceId { get; set; } = "";
   public string cInvoiceNo { get; set; } = "";
   public decimal cInvoiceTotal { get; set; }
   public DateTime ustamp { get; set; }
}

public class LogStoreIntegrationTests(SqlServerDatabase sql)
{
   public static TheoryData<string> Providers => ["mssql", "postgresql", "mysql"];

   private static CancellationToken Ct => TestContext.Current.CancellationToken;

   [Theory]
   [MemberData(nameof(Providers))]
   public async Task PrepareCreatesTheDatabaseIsIdempotentAndVerifies(string providerName) {
      var target = LogTarget.For(providerName, sql);
      var created = new List<string>();
      try {
         var name = await target.PrepareStoreAsync(created, Ct);
         var again = await LogStorePrepare.RunAsync(new LogStorePrepareRequest { Store = target.Draft(name), OwnerUser = target.Draft(name).User,
            OwnerPassword = target.Draft(name).Password, CreateDatabase = true }, target.Draft(name).Password, Ct);
         Assert.True(again.Success);
         Assert.Equal(LogPrepareStepStatus.Skipped, again.Steps.Single(s => s.Name == "Create database").Status);
         Assert.Equal(LogStoreDbContext.SchemaVersion, await LogStartupCheck.CheckStoreAsync(target.Handle(name), Ct));
      }
      finally { await target.DropAsync(created); }
   }

   [Theory]
   [MemberData(nameof(Providers))]
   public async Task IngestGroupsErrorsAndPagesAcrossStoresWithoutDuplicates(string providerName) {
      var target = LogTarget.For(providerName, sql);
      var created = new List<string>();
      try {
         var write = await target.PrepareStoreAsync(created, Ct);
         var old = await target.PrepareStoreAsync(created, Ct);
         var core = target.Info(write);
         var runtime = new LogRuntime("Tests", "Test");
         var registry = new LogStoreRegistry(target.Handle(write), [target.Handle(old, LogStoreRole.Read)], false, core, [core], []);
         var ingest = new LogIngest(registry, runtime);
         var oldIngest = new LogIngest(new LogStoreRegistry(target.Handle(old), [], false, core, [core], []), runtime);
         var t0 = DateTime.UtcNow.AddMinutes(-30);
         string Trace(int line) => $"System.InvalidOperationException: boom\n   at Em.Invoice.Post() in X.cs:line {line}";

         await ingest.WriteAsync([
            new LogEvent { Time = t0.AddSeconds(1), Level = LogEventLevel.Information, Category = "Em.Test", Message = "one", RequestId = "r1" },
            new LogEvent { Time = t0.AddSeconds(2), Level = LogEventLevel.Warning, Category = "Em.Test", Message = "two" },
            new LogEvent { Time = t0.AddSeconds(3), Level = LogEventLevel.Error, Category = "Em.Test", Message = "three", Exception = Trace(1), UserId = "U1" },
            new LogEvent { Time = t0.AddSeconds(4), Level = LogEventLevel.Error, Category = "Em.Test", Message = "four", Exception = Trace(2), UserId = "U2" },
            new LogEvent { Time = t0.AddSeconds(4), Level = LogEventLevel.Information, Category = "Em.Api.Request", Kind = LogEventKind.Access, Message = "same time" }
         ], LogSender.Process, null, Ct);
         await oldIngest.WriteAsync([
            new LogEvent { Time = t0.AddSeconds(4), Level = LogEventLevel.Information, Category = "Em.Test", Message = "old same time" },
            new LogEvent { Time = t0, Level = LogEventLevel.Information, Category = "Em.Test", Message = "old first" }
         ], LogSender.Process, null, Ct);

         var query = new LogQuery(registry, runtime, new AuditReader(registry, AuditCatalog.Empty));
         var seen = new List<LogEvent>();
         string? cursor = null;
         do {
            var page = await query.EventsAsync(t0.AddMinutes(-1), t0.AddMinutes(10), null, null, null, null, null, null, cursor, 2, Ct);
            Assert.Empty(page.Warnings);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
         } while (cursor is not null);
         Assert.Equal(7, seen.Count);
         Assert.Equal(seen.OrderByDescending(e => e.Time).Select(e => e.Time), seen.Select(e => e.Time));
         Assert.Equal("old first", seen[^1].Message);
         Assert.Single((await query.EventsAsync(t0.AddMinutes(-1), t0.AddMinutes(10), null, null, null, "ne", null, null, null, 50, Ct)).Items);

         var errors = await query.ErrorsAsync(t0.AddMinutes(-1), t0.AddMinutes(10), null, null, 50, Ct);
         var group = Assert.Single(errors.Items);
         Assert.Equal(2, group.Count);
         Assert.Equal(2, group.AffectedUsers);
         await query.SetErrorStateAsync(group.Fingerprint, LogErrorState.Resolved, Ct);
         await ingest.WriteAsync([new LogEvent { Time = DateTime.UtcNow, Level = LogEventLevel.Error, Category = "Em.Test", Message = "again", Exception = Trace(9) }],
            LogSender.User, "U1", Ct);
         // A client batch is stored with its events in one transaction; the same batch id again is a duplicate.
         var batchRow = () => new ta_LogIngestBatch { cLogIngestBatchId = "batch-1", cLogIngestBatchTime = DateTime.UtcNow, cLogIngestBatchEvents = 1 };
         var clientEvent = () => new LogEvent { Time = DateTime.UtcNow, Category = "Em.Ui", Message = "client", Source = "client" };
         Assert.Equal(1, await ingest.WriteAsync([clientEvent()], LogSender.User, "U1", batchRow(), Ct));
         Assert.Equal(-1, await ingest.WriteAsync([clientEvent()], LogSender.User, "U1", batchRow(), Ct));

         var detail = await query.ErrorDetailAsync(group.Fingerprint, Ct);
         Assert.Equal(LogErrorState.New, detail.Group.State);
         Assert.Equal(3, detail.Group.Count);
         Assert.Equal(3, detail.Recent.Length);
      }
      finally { await target.DropAsync(created); }
   }

   [Theory]
   [MemberData(nameof(Providers))]
   public async Task AuditFollowsTheTransactionIsMovedToTheStoreAndReadOnce(string providerName) {
      var target = LogTarget.For(providerName, sql);
      var created = new List<string>();
      try {
         var app = await target.CreateAppDatabaseAsync(created, Ct);
         var store = await target.PrepareStoreAsync(created, Ct);
         var core = target.Info(app);
         var registry = new LogStoreRegistry(target.Handle(store), [], false, core, [core], []);
         var builder = new EmAppBuilder { Services = new ServiceCollection() };
         builder.AddAudit("Invoice", a => a.Entry("created", "Created invoice"));
         var catalog = new AuditCatalog(builder.AuditGroups);
         var writer = new AuditWriter(catalog, registry, new LogRuntime("Tests", "Test"), new ServiceCollection().BuildServiceProvider(), new HttpContextAccessor());

         var options = new DbContextOptionsBuilder<LogTestInvoiceContext>();
         options.UseEmProvider(target.Db, core.ConnectionString);
         await using (var ctx = new LogTestInvoiceContext(options.Options)) {
            var audit = writer.Use(ctx);
            ctx.Invoices.Add(new LogTestInvoice { cInvoiceId = "INV-1", cInvoiceNo = "A-1", cInvoiceTotal = 10, ustamp = DateTime.UtcNow });
            audit.AddEntry("Invoice", "INV-1", "created", docNo: "A-1");
            await ctx.SaveChangesAsync(Ct);

            // Rolled back with its data: the entry must not survive.
            await using (var tx = await ctx.Database.BeginTransactionAsync(Ct)) {
               ctx.Invoices.Add(new LogTestInvoice { cInvoiceId = "INV-2", cInvoiceNo = "A-2", cInvoiceTotal = 20, ustamp = DateTime.UtcNow });
               audit.AddEntry("Invoice", "INV-2", "created", docNo: "A-2");
               await ctx.SaveChangesAsync(Ct);
               await tx.RollbackAsync(Ct);
            }
         }
         Assert.Equal(0, writer.PendingCount);
         Assert.Equal(1, await target.CountAsync(app, "ta_LogAudit", $"{target.Col("cLogAuditShipState")} = 1", Ct));
         Assert.Equal(1, await target.CountAsync(app, "Invoices", "1 = 1", Ct));

         await writer.WriteNowAsync(new AuditEntryDraft { Group = "Invoice", Key = "INV-1", Kind = "created", Summary = "written now" }, Ct);

         var reader = new AuditReader(registry, catalog);
         var before = await reader.QueryAsync(new AuditQuery { Groups = ["Invoice"] }, Ct);
         Assert.Equal(2, before.Items.Length);
         Assert.Equal("Created invoice", before.Items[0].KindLabel);

         var shipper = new AuditShipper(registry, NullLogger<AuditShipper>.Instance);
         Assert.Equal(0, await shipper.ShipAllAsync(Ct));
         Assert.Equal(0, await target.CountAsync(app, "ta_LogAudit", "1 = 1", Ct));
         Assert.Equal(2, await target.CountAsync(store, "ta_LogAudit", "1 = 1", Ct));
         await shipper.ShipAllAsync(Ct);
         Assert.Equal(2, await target.CountAsync(store, "ta_LogAudit", "1 = 1", Ct));

         var first = await reader.ByDocAsync("Invoice", "INV-1", null, 1, Ct);
         var second = await reader.ByDocAsync("Invoice", "INV-1", first.NextCursor, 1, Ct);
         Assert.NotNull(first.NextCursor);
         Assert.Null(second.NextCursor);
         Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
         Assert.Empty(await reader.SinceAsync("Invoice", "INV-1", first.Items[0].Time, first.Items[0].Id, Ct));
      }
      finally { await target.DropAsync(created); }
   }

   [Theory]
   [MemberData(nameof(Providers))]
   public async Task RetentionDeletesOnlyExpiredRows(string providerName) {
      var target = LogTarget.For(providerName, sql);
      var created = new List<string>();
      try {
         var app = await target.CreateAppDatabaseAsync(created, Ct);
         var store = await target.PrepareStoreAsync(created, Ct);
         var core = target.Info(app);
         var registry = new LogStoreRegistry(target.Handle(store), [], false, core, [core], []);
         var ingest = new LogIngest(registry, new LogRuntime("Tests", "Test"));
         var now = DateTime.UtcNow;
         var events = Enumerable.Range(0, 30).Select(i => new LogEvent {
            Time = now.AddDays(i < 20 ? -10 : -1), Level = LogEventLevel.Information, Category = "Em.Test", Message = $"e{i}"
         }).Append(new LogEvent { Time = now.AddDays(-40), Level = LogEventLevel.Error, Category = "Em.Test", Message = "old error" })
           .Append(new LogEvent { Time = now.AddDays(-40), Kind = LogEventKind.Access, Level = LogEventLevel.Information, Category = "Em.Api.Request", Message = "old access" })
           .ToList();
         await ingest.WriteAsync(events, LogSender.Process, null, Ct);

         var settings = new LogStoreSettings(() => {
            var o = new DbContextOptionsBuilder<ApiCoreContext>();
            o.UseEmProvider(core.Provider, core.ConnectionString);
            return new ApiCoreContext(o.Options);
         });
         var builder = new EmAppBuilder { Services = new ServiceCollection() };
         builder.AddLogServer();
         var retention = new LogRetention(registry, settings, new AuditCatalog(builder.AuditGroups), NullLogger<LogRetention>.Instance);
         var preview = await retention.PreviewAsync(new LogRetentionRules(), Ct);
         Assert.Equal(20, preview.Items.Single(i => i.Scope == "Telemetry Information").Rows);
         Assert.Equal(1, preview.Items.Single(i => i.Scope == "Access").Rows);
         var summary = await retention.RunAsync(Ct);
         Assert.Contains("Telemetry Information: 20", summary);
         Assert.Equal(11, await target.CountAsync(store, "ta_LogEvent", "1 = 1", Ct)); // 10 recent + the 40-day error (kept 90 days)
         Assert.NotNull((await retention.ViewAsync(Ct)).LastRunUtc);
      }
      finally { await target.DropAsync(created); }
   }
}
