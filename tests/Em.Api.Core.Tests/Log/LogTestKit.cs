using Em.Api.Core.Log;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Api.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.Tests.Log;

/// <summary>Builders shared by the log engine unit tests; nothing here opens a database.</summary>
internal static class LogTestKit
{
   public static readonly DbConnectionInfo Core = new("Default", "Server=core.invalid;Database=Core;TrustServerCertificate=True", DatabaseProvider.MicrosoftSqlServer);
   public static readonly DbConnectionInfo Store = new("Store", "Server=store.invalid;Database=Logs;TrustServerCertificate=True", DatabaseProvider.MicrosoftSqlServer);

   public static LogStoreRegistry Registry(bool internalStore = true) {
      var write = internalStore
         ? new LogStoreHandle(LogStoreDocument.InternalStoreId, "Internal", LogStoreKind.Internal, LogStoreRole.Write, Core.Provider, Core.ConnectionString)
         : new LogStoreHandle("S1", "Logs", LogStoreKind.Connection, LogStoreRole.Write, Store.Provider, Store.ConnectionString);
      return new LogStoreRegistry(write, [], false, Core, [Core], []);
   }

   public static AuditCatalog Catalog() {
      var builder = new EmAppBuilder { Services = new ServiceCollection() };
      builder.AddAudit("Invoice", a => a.Title("Invoice")
         .Entry("created", "Created invoice")
         .Entry("voided", "Voided invoice", requireReason: true)
         .Entry("exported", "Exported invoice", fromClient: true)
         .CanReadClaim("Invoice:View")
         .MinRetention(TimeSpan.FromDays(3653)));
      return new AuditCatalog(builder.AuditGroups.Concat(SystemAuditCatalog.Create()));
   }

   public static AuditWriter Writer(LogStoreRegistry? registry = null) {
      var services = new ServiceCollection().BuildServiceProvider();
      return new AuditWriter(Catalog(), registry ?? Registry(), new LogRuntime("Tests", "Test"), services, new HttpContextAccessor());
   }

   /// <summary>A context with one table of its own; model only, never connected.</summary>
   public sealed class InvoiceContext(DbContextOptions<InvoiceContext> options) : EmDbContext(options)
   {
      public DbSet<Invoice> Invoices => Set<Invoice>();

      public static InvoiceContext Create(string connectionString) {
         var options = new DbContextOptionsBuilder<InvoiceContext>();
         options.UseEmProvider(DatabaseProvider.MicrosoftSqlServer, connectionString);
         return new InvoiceContext(options.Options);
      }
   }

   public sealed class Invoice
   {
      [System.ComponentModel.DataAnnotations.Key] public string cInvoiceId { get; set; } = "";
      [System.ComponentModel.DataAnnotations.Display(Name = "Number")] public string cInvoiceNo { get; set; } = "";
      public decimal cInvoiceTotal { get; set; }
      public string? cInvoiceSecret { get; set; }
      public DateTime ustamp { get; set; }
   }
}
