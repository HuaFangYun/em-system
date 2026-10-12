using Em.Api.Core.Log;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Tests.Log;

public class AuditWriterTests
{
   [Fact]
   public void ParkedEntriesJoinTheFirstContextThatSaves() {
      var writer = LogTestKit.Writer();
      using var ctx = LogTestKit.InvoiceContext.Create(LogTestKit.Core.ConnectionString);
      writer.AddEntry("Invoice", "INV-1", "created");
      writer.OnSaving(ctx);
      var row = Assert.Single(ctx.ChangeTracker.Entries<ta_LogAudit>()).Entity;
      Assert.Equal(EntityState.Added, ctx.Entry(row).State);
      Assert.Equal(0, row.cLogAuditShipState); // the core database is the store
      writer.OnSaved(ctx);
      Assert.Equal(0, writer.PendingCount);
   }

   [Fact]
   public void ASecondContextSavingWithUndirectedEntriesIsRefused() {
      var writer = LogTestKit.Writer();
      using var first = LogTestKit.InvoiceContext.Create(LogTestKit.Core.ConnectionString);
      using var second = LogTestKit.InvoiceContext.Create(LogTestKit.Store.ConnectionString);
      writer.AddEntry("Invoice", "INV-1", "created");
      writer.OnSaving(first);
      writer.OnSaved(first);
      writer.AddEntry("Invoice", "INV-2", "created");
      var error = Assert.Throws<InvalidOperationException>(() => writer.OnSaving(second));
      Assert.Contains("Invoice/created (INV-2)", error.Message);
      Assert.Contains("Use(context)", error.Message);
   }

   [Fact]
   public void UseDirectsEntriesToOneContextOnly() {
      var writer = LogTestKit.Writer();
      using var first = LogTestKit.InvoiceContext.Create(LogTestKit.Core.ConnectionString);
      using var second = LogTestKit.InvoiceContext.Create(LogTestKit.Store.ConnectionString);
      writer.Use(second).AddEntry("Invoice", "INV-1", "created");
      writer.OnSaving(first);
      Assert.Empty(first.ChangeTracker.Entries<ta_LogAudit>());
      writer.OnSaving(second);
      Assert.Single(second.ChangeTracker.Entries<ta_LogAudit>());
   }

   [Fact]
   public void EntriesInAnotherDatabaseWaitForTheShipper() {
      var writer = LogTestKit.Writer(LogTestKit.Registry(internalStore: false));
      using var ctx = LogTestKit.InvoiceContext.Create(LogTestKit.Core.ConnectionString);
      writer.AddEntry("Invoice", "INV-1", "created");
      writer.OnSaving(ctx);
      Assert.Equal(1, ctx.ChangeTracker.Entries<ta_LogAudit>().Single().Entity.cLogAuditShipState);
   }

   [Fact]
   public void AFailedSaveKeepsTheEntryParked() {
      var writer = LogTestKit.Writer();
      using var ctx = LogTestKit.InvoiceContext.Create(LogTestKit.Core.ConnectionString);
      writer.AddEntry("Invoice", "INV-1", "created");
      writer.OnSaving(ctx);
      writer.OnFailed(ctx);
      Assert.Equal(1, writer.PendingCount);
      Assert.Empty(ctx.ChangeTracker.Entries<ta_LogAudit>());
      writer.OnSaving(ctx);
      Assert.Single(ctx.ChangeTracker.Entries<ta_LogAudit>());
   }

   [Fact]
   public void TheLogEnginesOwnContextsNeverTakeModuleEntries() {
      var writer = LogTestKit.Writer();
      using var outbox = LogOutboxDbContext.Create(LogTestKit.Core);
      writer.AddEntry("Invoice", "INV-1", "created");
      writer.OnSaving(outbox);
      Assert.Empty(outbox.ChangeTracker.Entries<ta_LogAudit>());
      Assert.Equal(1, writer.PendingCount);
   }
}
