using Em.Ui.Wpf.Shared;

namespace Em.Ui.Wpf.Core.Tests;

public class NumericInputTests
{
   [Theory]
   [InlineData("", true)]
   [InlineData("0", true)]
   [InlineData("36500", true)]
   [InlineData("12a", false)]
   [InlineData("abc", false)]
   [InlineData("-5", false)]
   [InlineData("1 0", false)]
   [InlineData("1.5", false)]
   public void IntegerAcceptsDigitsOnly(string text, bool expected) =>
      Assert.Equal(expected, NumericInput.IsAcceptable(text, NumericInputKind.Integer, "."));

   [Theory]
   [InlineData("", true)]
   [InlineData("12", true)]
   [InlineData("0,5", true)]
   [InlineData(",", true)]
   [InlineData("1,2,3", false)]
   [InlineData("1.5", false)]
   [InlineData("5MB", false)]
   public void DecimalAcceptsOneSeparatorOfTheCulture(string text, bool expected) =>
      Assert.Equal(expected, NumericInput.IsAcceptable(text, NumericInputKind.Decimal, ","));

   [Fact]
   public void NoneAcceptsAnything() =>
      Assert.True(NumericInput.IsAcceptable("abc", NumericInputKind.None, "."));
}
