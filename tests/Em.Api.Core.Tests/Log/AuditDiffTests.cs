using Em.Api.Core.Log.Audit;

namespace Em.Api.Core.Tests.Log;

public class AuditDiffTests
{
   private static LogTestKit.Invoice Inv(string no, decimal total, string? secret = null) =>
      new() { cInvoiceId = "INV-1", cInvoiceNo = no, cInvoiceTotal = total, cInvoiceSecret = secret, ustamp = DateTime.UtcNow };

   [Fact]
   public void OnlyChangedFieldsAreListedWithTheirDisplayLabel() {
      var changes = AuditDiff.Of(Inv("A-1", 10m), Inv("A-2", 10m));
      var change = Assert.Single(changes);
      Assert.Equal("cInvoiceNo", change.Field);
      Assert.Equal("Number", change.Label);
      Assert.Equal("A-1", change.Old);
      Assert.Equal("A-2", change.New);
   }

   [Fact]
   public void StampsAndIgnoredFieldsAreSkipped() {
      Assert.Empty(AuditDiff.Of(Inv("A", 1m), Inv("A", 2m), ignore: ["cInvoiceTotal"]));
   }

   [Fact]
   public void SecretFieldsNeverCarryTheirValue() {
      var change = Assert.Single(AuditDiff.Of(Inv("A", 1m, "old"), Inv("A", 1m, "new"), secret: ["cInvoiceSecret"]));
      Assert.True(change.Secret);
      Assert.Null(change.Old);
      Assert.Equal(AuditDiff.SecretChanged, change.New);
      Assert.Equal(AuditDiff.SecretSet, AuditDiff.Of(Inv("A", 1m), Inv("A", 1m, "x"), secret: ["cInvoiceSecret"]).Single().New);
   }

   [Fact]
   public void InsertListsEveryValueAndNumbersUseTheInvariantCulture() {
      var changes = AuditDiff.Of(null, Inv("A", 1234.5m));
      Assert.Contains(changes, c => c.Field == "cInvoiceTotal" && c.New == "1234.5" && c.Old is null);
   }

   [Fact]
   public void KeysComposeInPrimaryKeyOrder() {
      Assert.Equal("U1", AuditKey.Compose("U1"));
      Assert.Equal("[\"U1\",\"R-FIN\"]", AuditKey.Compose("U1", "R-FIN"));
      using var ctx = LogTestKit.InvoiceContext.Create(LogTestKit.Core.ConnectionString);
      Assert.Equal("INV-9", AuditKey.Of(ctx, new LogTestKit.Invoice { cInvoiceId = "INV-9" }));
   }
}
