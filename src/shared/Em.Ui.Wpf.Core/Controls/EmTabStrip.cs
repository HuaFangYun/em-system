using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ListBox = System.Windows.Controls.ListBox;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Em.Ui.Wpf.Controls;

/// <summary>A horizontal, reorderable tab selector with an optional leading content slot.</summary>
public class EmTabStrip : ListBox
{
   private ListBoxItem? _dragContainer;
   private object? _dragItem;
   private Point _start;
   private bool _dragging;
   private bool _moving;

   static EmTabStrip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(EmTabStrip), new FrameworkPropertyMetadata(typeof(EmTabStrip)));

   /// <summary>Identifies <see cref="TitlePath"/>.</summary>
   public static readonly DependencyProperty TitlePathProperty = DependencyProperty.Register(nameof(TitlePath), typeof(string), typeof(EmTabStrip), new PropertyMetadata("Title", TitlePathChanged));
   /// <summary>Identifies <see cref="CloseCommand"/>.</summary>
   public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(EmTabStrip));
   /// <summary>Identifies <see cref="CanReorder"/>.</summary>
   public static readonly DependencyProperty CanReorderProperty = DependencyProperty.Register(nameof(CanReorder), typeof(bool), typeof(EmTabStrip), new PropertyMetadata(true));
   /// <summary>Identifies <see cref="MinItemWidth"/>.</summary>
   public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(EmTabStrip), new FrameworkPropertyMetadata(130d, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is double width && double.IsFinite(width) && width >= 0);
   /// <summary>Identifies <see cref="LeadingContent"/>.</summary>
   public static readonly DependencyProperty LeadingContentProperty = DependencyProperty.Register(nameof(LeadingContent), typeof(object), typeof(EmTabStrip));
   /// <summary>Identifies <see cref="LeadingContentTemplate"/>.</summary>
   public static readonly DependencyProperty LeadingContentTemplateProperty = DependencyProperty.Register(nameof(LeadingContentTemplate), typeof(DataTemplate), typeof(EmTabStrip));
   /// <summary>Identifies <see cref="ItemMoved"/>.</summary>
   public static readonly RoutedEvent ItemMovedEvent = EventManager.RegisterRoutedEvent(nameof(ItemMoved), RoutingStrategy.Bubble, typeof(EventHandler<EmTabStripItemMovedEventArgs>), typeof(EmTabStrip));

   /// <summary>The binding path used for each tab's title and tooltip.</summary>
   public string TitlePath { get => (string)GetValue(TitlePathProperty); set => SetValue(TitlePathProperty, value); }
   /// <summary>The command invoked by a close button or a middle click, with the item as its parameter.</summary>
   public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }
   /// <summary>Whether dragging can reorder tabs.</summary>
   public bool CanReorder { get => (bool)GetValue(CanReorderProperty); set => SetValue(CanReorderProperty, value); }
   /// <summary>The minimum width used when the panel shrinks long tabs.</summary>
   public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
   /// <summary>Optional content outside the scrollable tabs; null reserves no space.</summary>
   public object? LeadingContent { get => GetValue(LeadingContentProperty); set => SetValue(LeadingContentProperty, value); }
   /// <summary>The template for <see cref="LeadingContent"/>.</summary>
   public DataTemplate? LeadingContentTemplate { get => (DataTemplate?)GetValue(LeadingContentTemplateProperty); set => SetValue(LeadingContentTemplateProperty, value); }
   /// <summary>Reports a move, or requests one when the source cannot be modified by the control.</summary>
   public event EventHandler<EmTabStripItemMovedEventArgs> ItemMoved { add => AddHandler(ItemMovedEvent, value); remove => RemoveHandler(ItemMovedEvent, value); }

   /// <inheritdoc/>
   protected override void PrepareContainerForItemOverride(DependencyObject element, object item) {
      base.PrepareContainerForItemOverride(element, item);
      if (element is ListBoxItem container) BindTitle(container, item);
   }

   private void BindTitle(ListBoxItem container, object item) => container.SetBinding(ToolTipProperty, new Binding(TitlePath) { Source = item });
   private static void TitlePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
      var strip = (EmTabStrip)d;
      foreach (var item in strip.Items)
         if (strip.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) strip.BindTitle(container, item);
   }

   /// <summary>Moves an item without changing the active tab, and raises <see cref="ItemMoved"/>.</summary>
   /// <returns>True when a valid move was performed or requested.</returns>
   public bool MoveItem(int from, int to) {
      if (!CanReorder || from < 0 || to < 0 || from >= Items.Count || to >= Items.Count || from == to) return false;
      var item = Items[from];
      var selected = SelectedItem;
      var list = ItemsSource as IList ?? (ItemsSource is null ? Items : null);
      if (list is { IsReadOnly: false, IsFixedSize: false } && !TryMove(list, from, to)) return false;
      SetCurrentValue(SelectedItemProperty, selected);
      RaiseEvent(new EmTabStripItemMovedEventArgs(ItemMovedEvent, item, from, to));
      return true;
   }

   /// <summary>Moves an item in a writable list, preserving observable collection move notifications.</summary>
   /// <returns>False for invalid indices, unchanged indices, or immutable lists.</returns>
   public static bool TryMove(IList list, int from, int to) {
      ArgumentNullException.ThrowIfNull(list);
      if (list.IsReadOnly || list.IsFixedSize || from < 0 || to < 0 || from >= list.Count || to >= list.Count || from == to) return false;
      var move = list.GetType().GetMethod("Move", BindingFlags.Instance | BindingFlags.Public, [typeof(int), typeof(int)]);
      if (move is not null) move.Invoke(list, [from, to]);
      else { var item = list[from]; list.RemoveAt(from); list.Insert(to, item); }
      return true;
   }

   /// <inheritdoc/>
   protected override void OnPreviewMouseDown(MouseButtonEventArgs e) {
      if (Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } container) {
         var item = ItemContainerGenerator.ItemFromContainer(container);
         if (e.ChangedButton == MouseButton.Middle) {
            if (CloseCommand?.CanExecute(item) == true) CloseCommand.Execute(item);
            e.Handled = true;
         }
         else if (e.ChangedButton == MouseButton.Left && CanReorder && Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) is null) {
            _dragContainer = container;
            _dragItem = item;
            _start = e.GetPosition(this);
         }
      }
      base.OnPreviewMouseDown(e);
   }

   /// <inheritdoc/>
   protected override void OnPreviewMouseMove(MouseEventArgs e) {
      base.OnPreviewMouseMove(e);
      if (_dragContainer is null || _dragItem is null) return;
      if (!CanReorder || e.LeftButton != MouseButtonState.Pressed) { FinishDrag(); return; }
      var point = e.GetPosition(this);
      if (!_dragging) {
         if (Math.Abs(point.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance) return;
         _dragging = true;
         _dragContainer.CaptureMouse();
      }
      var from = Items.IndexOf(_dragItem);
      for (var i = 0; i < Items.Count; i++) {
         if (i == from || ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem other) continue;
         var bounds = other.TransformToAncestor(this).TransformBounds(new Rect(other.RenderSize));
         if ((i > from && point.X >= bounds.Left + bounds.Width / 2) || (i < from && point.X <= bounds.Left + bounds.Width / 2)) {
            _moving = true;
            try {
               MoveItem(from, i);
               UpdateLayout();
               if (ItemContainerGenerator.ContainerFromItem(_dragItem) is ListBoxItem current) _dragContainer = current;
               _dragContainer?.CaptureMouse();
            }
            finally { _moving = false; }
            break;
         }
      }
      e.Handled = true;
   }

   /// <inheritdoc/>
   protected override void OnPreviewMouseUp(MouseButtonEventArgs e) {
      if (e.ChangedButton == MouseButton.Left) { var dragging = _dragging; FinishDrag(); e.Handled |= dragging; }
      base.OnPreviewMouseUp(e);
   }
   /// <inheritdoc/>
   protected override void OnLostMouseCapture(MouseEventArgs e) {
      if (!_moving && e.OriginalSource == _dragContainer) FinishDrag();
      base.OnLostMouseCapture(e);
   }
   /// <inheritdoc/>
   protected override void OnPreviewKeyDown(KeyEventArgs e) {
      if (e.Key == Key.Escape && _dragging) { FinishDrag(); e.Handled = true; }
      base.OnPreviewKeyDown(e);
   }
   private void FinishDrag() {
      var container = _dragContainer;
      _dragContainer = null; _dragItem = null; _dragging = false;
      if (container?.IsMouseCaptured == true) container.ReleaseMouseCapture();
   }
   private T? Ancestor<T>(DependencyObject? element) where T : DependencyObject {
      while (element is not null && element != this) {
         if (element is T match) return match;
         element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
      }
      return null;
   }
}

/// <summary>Describes a completed or requested tab move.</summary>
public sealed class EmTabStripItemMovedEventArgs(RoutedEvent routedEvent, object item, int oldIndex, int newIndex) : RoutedEventArgs(routedEvent)
{
   /// <summary>The tab item being moved.</summary>
   public object Item { get; } = item;
   /// <summary>The original index.</summary>
   public int OldIndex { get; } = oldIndex;
   /// <summary>The destination index.</summary>
   public int NewIndex { get; } = newIndex;
}
