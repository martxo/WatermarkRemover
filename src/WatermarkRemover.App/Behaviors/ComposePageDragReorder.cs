using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App.Behaviors;

/// <summary>
/// Horizontal drag reordering for the 手机传文件 compose gallery. The tile's rotate buttons and
/// checkbox are excluded (they are clicks, not drags); dropping on the left/right half of a tile
/// inserts before/after it. The behaviour only translates pointer input and invokes the bound
/// move command, so the view model remains the single owner of the page order.
/// </summary>
public static class ComposePageDragReorder
{
    private const string DragFormat = "WatermarkRemover.ComposePage";

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ComposePageDragReorder),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Command receiving a <see cref="ComposePageMove"/> (From, To) on drop.</summary>
    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.RegisterAttached(
        "MoveCommand", typeof(ICommand), typeof(ComposePageDragReorder),
        new PropertyMetadata(null));

    private static Point _pressPoint;
    private static ComposeSourceViewModel? _pressedItem;
    private static ItemsControl? _pressOwner;

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static ICommand? GetMoveCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(MoveCommandProperty);

    public static void SetMoveCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(MoveCommandProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items) return;

        if ((bool)e.NewValue)
        {
            items.AllowDrop = true;
            items.PreviewMouseLeftButtonDown += OnMouseDown;
            items.PreviewMouseLeftButtonUp += OnMouseUp;
            items.PreviewMouseMove += OnMouseMove;
            items.DragEnter += OnDragHeartbeat;
            items.DragOver += OnDragHeartbeat;
            items.Drop += OnDrop;
        }
        else
        {
            items.PreviewMouseLeftButtonDown -= OnMouseDown;
            items.PreviewMouseLeftButtonUp -= OnMouseUp;
            items.PreviewMouseMove -= OnMouseMove;
            items.DragEnter -= OnDragHeartbeat;
            items.DragOver -= OnDragHeartbeat;
            items.Drop -= OnDrop;
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetPress();
        if (e.ChangedButton != MouseButton.Left || sender is not ItemsControl items) return;

        // Rotate / checkbox controls live inside the tile and must stay click-only.
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindItem(e.OriginalSource as DependencyObject) is not { } hit) return;

        _pressedItem = hit.Item;
        _pressOwner = items;
        _pressPoint = e.GetPosition(items);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e) => ResetPress();

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || _pressOwner is null || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed) ResetPress();
            return;
        }

        Point position = e.GetPosition(_pressOwner);
        if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ItemsControl dragSource = _pressOwner;
        ComposeSourceViewModel item = _pressedItem;
        ResetPress();
        DragDrop.DoDragDrop(dragSource, new DataObject(DragFormat, item), DragDropEffects.Move);
    }

    private static void OnDragHeartbeat(object sender, DragEventArgs e)
    {
        bool canDrop = e.Data.GetDataPresent(DragFormat) &&
                       FindItem(e.OriginalSource as DependencyObject) is not null;
        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not ComposeSourceViewModel dragged) return;
        if (FindItem(e.OriginalSource as DependencyObject) is not { } target) return;
        if (sender is not ItemsControl items) return;

        ICommand? command = GetMoveCommand(items);
        if (command is null) return;

        int from = items.Items.IndexOf(dragged);
        int targetIndex = items.Items.IndexOf(target.Item);
        if (from < 0 || targetIndex < 0) return;

        Point position = e.GetPosition(target.Element);
        bool after = position.X > target.Element.ActualWidth / 2;
        int insertAt = targetIndex + (after ? 1 : 0);
        int to = from < insertAt ? insertAt - 1 : insertAt;
        if (to == from) return;

        var request = new ComposePageMove(from, to);
        if (command.CanExecute(request)) command.Execute(request);
    }

    private static void ResetPress()
    {
        _pressedItem = null;
        _pressOwner = null;
    }

    /// <summary>The tile under the pointer; stops at the owning ItemsControl so containers are not mistaken for tiles.</summary>
    private static (ComposeSourceViewModel Item, FrameworkElement Element)? FindItem(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ItemsControl) return null;
            if (current is FrameworkElement { DataContext: ComposeSourceViewModel item } element)
            {
                return (item, element);
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }

        return null;
    }
}
