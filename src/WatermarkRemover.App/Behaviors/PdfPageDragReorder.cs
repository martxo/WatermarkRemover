using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App.Behaviors;

/// <summary>
/// Drag-and-drop page reordering for a page-tile <see cref="ItemsControl"/>: press a tile, drag
/// it over another tile (left/right half decides before/after in the horizontal strip, top/
/// bottom half in the vertical viewer rail) and drop. The view model writes the new order into
/// the PDF and re-points the tiles; this class only translates pointer input, so the same
/// behaviour serves the inline strip and the fullscreen film rail.
///
/// Deliberately attached rather than wired in each window's code-behind: both surfaces share one
/// implementation and cannot drift apart.
/// </summary>
public static class PdfPageDragReorder
{
    private const string DragFormat = "WatermarkRemover.PdfPageTile";

    /// <summary>Turns reordering on for one page-tile ItemsControl.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PdfPageDragReorder),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Which half of a tile decides "before / after": horizontal strips compare the
    /// pointer's X, vertical rails its Y.</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.RegisterAttached(
        "Orientation", typeof(Orientation), typeof(PdfPageDragReorder),
        new PropertyMetadata(Orientation.Horizontal));

    private static Point _pressPoint;
    private static PdfPreviewThumbnailViewModel? _pressedTile;
    private static ItemsControl? _pressOwner;

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static Orientation GetOrientation(DependencyObject element) =>
        (Orientation)element.GetValue(OrientationProperty);

    public static void SetOrientation(DependencyObject element, Orientation value) =>
        element.SetValue(OrientationProperty, value);

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

        // A press on a tile's corner action (rotate / delete) is a click, never a drag; the
        // tile's main click surface is itself a Button (it opens the viewer), and that one
        // must stay draggable.
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is { } button
            && button.DataContext is PdfPreviewThumbnailViewModel buttonTile
            && !ReferenceEquals(button.Command, buttonTile.SelectCommand))
            return;
        if (items.DataContext is PdfPreviewViewModel { IsRotating: true } or PdfPreviewViewModel { IsOrienting: true })
            return;

        if (FindTile(e.OriginalSource as DependencyObject) is not { } hit) return;
        _pressedTile = hit.Tile;
        _pressOwner = items;
        _pressPoint = e.GetPosition(items);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e) => ResetPress();

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedTile is null || _pressOwner is null || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed) ResetPress();
            return;
        }

        Point position = e.GetPosition(_pressOwner);
        if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        PdfPreviewThumbnailViewModel tile = _pressedTile;
        ResetPress();

        var data = new DataObject(DragFormat, tile);
        DragDrop.DoDragDrop(_pressOwner, data, DragDropEffects.Move);
    }

    private static void OnDragHeartbeat(object sender, DragEventArgs e)
    {
        bool canDrop = e.Data.GetDataPresent(DragFormat)
            && FindTile(e.OriginalSource as DependencyObject) is not null;
        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not PdfPreviewThumbnailViewModel dragged) return;
        if (FindTile(e.OriginalSource as DependencyObject) is not { } target) return;
        if (sender is not ItemsControl items) return;
        if (items.DataContext is not PdfPreviewViewModel vm) return;

        Orientation orientation = GetOrientation(items);
        Point position = e.GetPosition(target.Element);
        bool after = orientation == Orientation.Vertical
            ? position.Y > target.Element.ActualHeight / 2
            : position.X > target.Element.ActualWidth / 2;

        // The dragged page is lifted out first, so dropping AFTER a later tile lands one index
        // earlier in the resulting list.
        int insertAt = target.Tile.Index + (after ? 1 : 0);
        int from = dragged.Index;
        int to = from < insertAt ? insertAt - 1 : insertAt;
        if (to == from) return;

        _ = vm.MovePageAsync(from, to);
    }

    private static void ResetPress()
    {
        _pressedTile = null;
        _pressOwner = null;
    }

    /// <summary>The tile under the pointer: walks up only as far as the owning ItemsControl, so
    /// a row/section DataContext can never be mistaken for a page tile.</summary>
    private static (PdfPreviewThumbnailViewModel Tile, FrameworkElement Element)? FindTile(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ItemsControl) return null;
            if (current is FrameworkElement { DataContext: PdfPreviewThumbnailViewModel tile } element)
                return (tile, element);
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
