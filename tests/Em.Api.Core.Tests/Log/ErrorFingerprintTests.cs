using Em.Api.Core.Log;

namespace Em.Api.Core.Tests.Log;

public class ErrorFingerprintTests
{
   private const string Trace1 = """
      System.InvalidOperationException: Sequence contains no elements
         at System.Linq.ThrowHelper.ThrowNoElementsException()
         at Em.Invoice.InvoiceServices.PostInvoice_Post(String id) in C:\src\InvoiceServices.cs:line 42
         at Em.Api.Core.EmApp.InvokeAsync(Object service)
      """;

   private const string Trace2 = """
      System.InvalidOperationException: Sequence contains no elements (another message)
         at System.Linq.ThrowHelper.ThrowNoElementsException()
         at Em.Invoice.InvoiceServices.PostInvoice_Post(String id) in D:\build\InvoiceServices.cs:line 57
      """;

   [Fact]
   public void SameTypeAndFrameGiveTheSameFingerprintWhateverLineOrMessage() =>
      Assert.Equal(ErrorFingerprint.Compute(Trace1), ErrorFingerprint.Compute(Trace2));

   [Fact]
   public void TopFrameSkipsFrameworkFrames() =>
      Assert.Equal("Em.Invoice.InvoiceServices.PostInvoice_Post(String id)", ErrorFingerprint.TopFrameOf(Trace1));

   [Fact]
   public void TypeIsTheTextBeforeTheFirstColon() =>
      Assert.Equal("System.InvalidOperationException", ErrorFingerprint.TypeOf(Trace1));

   [Fact]
   public void DifferentTypesGiveDifferentFingerprints() =>
      Assert.NotEqual(ErrorFingerprint.Compute(Trace1), ErrorFingerprint.Compute(Trace1.Replace("InvalidOperationException", "ArgumentException")));

   [Fact]
   public void FingerprintIs32HexCharacters() => Assert.Matches("^[0-9a-f]{32}$", ErrorFingerprint.Compute("X: y"));
}
