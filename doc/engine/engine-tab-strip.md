# Tab strip and cascading menus

`EmTabStrip` is a horizontal selector for panels within one screen. It uses the same 40-pixel tab shape,
selection underline and scroll buttons as the desktop host. The host continues to use its existing strip.
There is no detach, reload, context menu or cross-window transfer behavior.

Merge `Em.Ui.Wpf.Core/Styles/MaterialDesign.xaml` for menu styles. The strip's default style loads automatically.

```xml
<controls:EmTabStrip ItemsSource="{Binding Tabs}" SelectedItem="{Binding ActiveTab}"
                     TitlePath="Title" CloseCommand="{Binding Commands[CloseTabCommand]}">
   <controls:EmTabStrip.LeadingContent>
      <ToggleButton Style="{StaticResource tabStripMenuButtonStyle}" Content="Panels" />
   </controls:EmTabStrip.LeadingContent>
</controls:EmTabStrip>
```

| Property | Default | Purpose |
| --- | --- | --- |
| `TitlePath` | `Title` | Item binding path for text and tooltip; updates with property notifications |
| `CloseCommand` | null | Command for the close button and middle click; parameter is the item |
| `CanReorder` | true | Enables mouse dragging |
| `MinItemWidth` | 130 | Minimum width used to shrink long tabs before scrolling |
| `LeadingContent` | null | Optional content to the left of the scrolling region; null reserves no space |
| `LeadingContentTemplate` | null | Template for the leading content |

The container tooltip binds to `TitlePath`; the tab text reads the same value. Item identity stays independent
of display text. Keep bindable screen state and commands in a view model derived from `MvvmModelBase`.

Use an `ObservableCollection<T>` for automatic reordering. The control calls its `Move(int, int)` method,
preserving a single move notification. Other writable `IList` sources use remove/insert. Unsupported or immutable
sources raise `ItemMoved` as a request, and the owner must perform the move. `ItemMoved` carries `Item`,
`OldIndex`, and `NewIndex`; selection is retained. `TryMove(IList, int, int)` exposes the collection operation.
Escape or lost mouse capture ends the drag at the current position. Buttons inside tabs do not start dragging.

For cascading menus, use the shared styles on the context menu and each menu item, including submenu items:

```xml
<ContextMenu Style="{StaticResource cascadeContextMenuStyle}"
             DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}">
   <MenuItem Style="{StaticResource cascadeMenuItemStyle}" Header="Panels">
      <MenuItem Style="{StaticResource cascadeMenuItemStyle}" Header="First panel"
                Command="{Binding Commands[OpenPanelCommand]}" />
   </MenuItem>
   <Separator />
</ContextMenu>
```

Set `HeaderTemplate="{StaticResource menuTitleSubtitleTemplate}"` with a header object having `Title` and
`Subtitle` for two-line rows. Empty subtitles collapse. A custom header can contain a trailing delete button.
The popup supports nested headers, checked items, separators and disabled rows. Connect the toggle's checked
state to menu open state in the consuming view; the strip does not create or manage a menu.

## Maintainer notes

Tests in `EmTabStripTests` cover move notifications, indices, immutable collections, active selection, title
binding, close button execution and middle-click command guards. `SharedStylesTests` loads shared menu keys.
The approved render harness is `..\.artefacts\em-system\scripts\tabstrip-render\`; its output stays in `out`.
