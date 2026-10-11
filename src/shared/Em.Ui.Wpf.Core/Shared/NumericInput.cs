using System.Globalization;
using System.Windows;
using System.Windows.Input;
using DataObject = System.Windows.DataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Em.Ui.Wpf.Shared
{
   /// <summary>What a <see cref="NumericInput"/> text box accepts.</summary>
   public enum NumericInputKind
   {
      /// <summary>Any text; the filter is off.</summary>
      None,

      /// <summary>Digits only.</summary>
      Integer,

      /// <summary>Digits and one decimal separator of the current culture.</summary>
      Decimal
   }

   /// <summary>
   /// Turns an ordinary <see cref="TextBox"/> into a number-only field without giving it a value of its own:
   /// typed and pasted characters other than digits (and, for <see cref="NumericInputKind.Decimal"/>, one
   /// decimal separator) are refused before they reach the text. The text may stay empty, so it suits
   /// optional limits where empty means "no limit"; use <c>NumericBox</c> when a value is always required.
   /// </summary>
   /// <remarks>
   /// <code>
   /// &lt;TextBox Style="{StaticResource fieldBoxStyle}" shared:NumericInput.Kind="Integer"
   ///          Text="{Binding KeepDays, UpdateSourceTrigger=PropertyChanged}" /&gt;
   /// </code>
   /// </remarks>
   public static class NumericInput
   {
      /// <summary>The attached property that selects what the text box accepts.</summary>
      public static readonly DependencyProperty KindProperty =
         DependencyProperty.RegisterAttached(
            "Kind",
            typeof(NumericInputKind),
            typeof(NumericInput),
            new FrameworkPropertyMetadata(NumericInputKind.None, OnKindChanged));

      /// <summary>Reads what a text box accepts.</summary>
      /// <param name="element">The text box to read.</param>
      /// <returns>The kind of number the text box accepts.</returns>
      public static NumericInputKind GetKind(DependencyObject element) =>
         (NumericInputKind)element.GetValue(KindProperty);

      /// <summary>Sets what a text box accepts.</summary>
      /// <param name="element">The text box to filter.</param>
      /// <param name="value">The kind of number the text box accepts.</param>
      public static void SetKind(DependencyObject element, NumericInputKind value) =>
         element.SetValue(KindProperty, value);

      /// <summary>
      /// Tells whether <paramref name="text"/> is acceptable while typing: empty, or digits with at most one
      /// <paramref name="decimalSeparator"/> when <paramref name="kind"/> is <see cref="NumericInputKind.Decimal"/>.
      /// </summary>
      /// <param name="text">The whole text as it would be after the change.</param>
      /// <param name="kind">What the field accepts.</param>
      /// <param name="decimalSeparator">The decimal separator of the culture the value is parsed with.</param>
      /// <returns><c>true</c> when the text may be kept.</returns>
      public static bool IsAcceptable(string text, NumericInputKind kind, string decimalSeparator) {
         if (kind == NumericInputKind.None) return true;
         var separators = 0;
         for (var i = 0; i < text.Length;) {
            if (char.IsAsciiDigit(text[i])) { i++; continue; }
            if (kind == NumericInputKind.Decimal && decimalSeparator.Length > 0 &&
                string.CompareOrdinal(text, i, decimalSeparator, 0, decimalSeparator.Length) == 0) {
               if (++separators > 1) return false;
               i += decimalSeparator.Length;
               continue;
            }
            return false;
         }
         return true;
      }

      private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
         if (d is not TextBox box) return;
         box.PreviewTextInput -= OnPreviewTextInput;
         box.PreviewKeyDown -= OnPreviewKeyDown;
         DataObject.RemovePastingHandler(box, OnPasting);
         if ((NumericInputKind)e.NewValue == NumericInputKind.None) return;
         box.PreviewTextInput += OnPreviewTextInput;
         box.PreviewKeyDown += OnPreviewKeyDown;
         DataObject.AddPastingHandler(box, OnPasting);
      }

      private static bool Accepts(TextBox box, string input) {
         var text = box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, input);
         return IsAcceptable(text, GetKind(box), CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
      }

      private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e) {
         if (!Accepts((TextBox)sender, e.Text)) e.Handled = true;
      }

      // Space is not text input in WPF, so PreviewTextInput never sees it.
      private static void OnPreviewKeyDown(object sender, KeyEventArgs e) {
         if (e.Key == Key.Space) e.Handled = true;
      }

      private static void OnPasting(object sender, DataObjectPastingEventArgs e) {
         if (e.DataObject.GetData(typeof(string)) is not string pasted || !Accepts((TextBox)sender, pasted.Trim()))
            e.CancelCommand();
      }
   }
}
