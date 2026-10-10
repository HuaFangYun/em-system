using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Em.Ui.Wpf.Controls;

namespace Em.Ui.Wpf.Core.Tests;

public class EmTabStripTests
{
   [Theory]
   [InlineData(0, 2, "BCA")]
   [InlineData(2, 0, "CAB")]
   public void TryMove_UsesObservableMove(int from, int to, string expected) {
      var list = new ObservableCollection<string>(["A", "B", "C"]);
      NotifyCollectionChangedAction? action = null;
      list.CollectionChanged += (_, e) => action = e.Action;
      Assert.True(EmTabStrip.TryMove(list, from, to));
      Assert.Equal(expected, string.Concat(list));
      Assert.Equal(NotifyCollectionChangedAction.Move, action);
   }
   [Theory]
   [InlineData(0, 0)] [InlineData(-1, 1)] [InlineData(0, 3)]
   public void TryMove_RejectsInvalidIndices(int from, int to) => Assert.False(EmTabStrip.TryMove(new ArrayList { "A", "B", "C" }, from, to));
   [Fact]
   public void TryMove_RejectsImmutableLists() {
      Assert.False(EmTabStrip.TryMove(ArrayList.ReadOnly(new ArrayList { "A", "B" }), 0, 1));
      Assert.False(EmTabStrip.TryMove(new[] { "A", "B" }, 0, 1));
   }
   [WpfFact]
   public void MoveItem_KeepsSelectionAndReportsIndices() {
      var list = new ObservableCollection<string>(["A", "B", "C"]);
      var strip = new EmTabStrip { ItemsSource = list, SelectedItem = "A" };
      EmTabStripItemMovedEventArgs? moved = null;
      strip.ItemMoved += (_, e) => moved = e;
      Assert.True(strip.MoveItem(0, 2));
      Assert.Equal("A", strip.SelectedItem);
      Assert.Equal("A", moved!.Item);
      Assert.Equal(0, moved.OldIndex); Assert.Equal(2, moved.NewIndex);
   }
   [WpfFact]
   public void ImmutableSource_RequestsMove() {
      var source = new[] { "A", "B" };
      var strip = new EmTabStrip { ItemsSource = source };
      var count = 0; strip.ItemMoved += (_, _) => count++;
      Assert.True(strip.MoveItem(0, 1)); Assert.Equal(1, count); Assert.Equal("A", source[0]);
   }
   [WpfFact]
   public void TitleAndCloseButton_UseItemAndCommand() {
      RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
      var command = new RecordingCommand();
      var item = new { Caption = "Panel" };
      var strip = new EmTabStrip { TitlePath = "Caption", CloseCommand = command, ItemsSource = new[] { item } };
      var resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/Em.Ui.Wpf.Core;component/Themes/Generic.xaml") };
      strip.Style = (Style)resources[typeof(EmTabStrip)];
      strip.Measure(new Size(500, 40)); strip.Arrange(new Rect(0, 0, 500, 40)); strip.UpdateLayout();
      var container = Assert.IsType<ListBoxItem>(strip.ItemContainerGenerator.ContainerFromIndex(0));
      Assert.Equal("Panel", container.ToolTip);
      container.ApplyTemplate();
      var close = Assert.IsType<Button>(container.Template.FindName("closeButton", container));
      strip.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
      typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(close, null);
      Assert.Same(item, command.Last);
      command.Last = null;
      container.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent });
      Assert.Same(item, command.Last);
      command.Last = null; command.Allowed = false;
      container.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent });
      Assert.Null(command.Last);
   }
   private sealed class RecordingCommand : ICommand {
      public object? Last;
      public bool Allowed = true;
      public bool CanExecute(object? parameter) => Allowed;
      public void Execute(object? parameter) => Last = parameter;
      public event EventHandler? CanExecuteChanged { add { } remove { } }
   }
}
