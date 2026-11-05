using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WatermarkRemover.App.Behaviors;

/// <summary>
/// Keeps a chat-style <see cref="ListBox"/> pinned to its newest row. The transfer page lives in
/// a data template, so its list has no code-behind name; this attached behavior owns the hook.
/// </summary>
public static class AutoScrollListBox
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(AutoScrollListBox),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached(
            "Handler", typeof(EventHandler), typeof(AutoScrollListBox),
            new PropertyMetadata(null));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;

        var handler = new EventHandler((_, _) =>
        {
            if (list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                ScrollToEnd(list);
        });

        if (e.NewValue is true)
        {
            list.ItemContainerGenerator.StatusChanged += handler;
            list.SetValue(HandlerProperty, handler);
            list.Loaded += OnLoaded;
        }
        else
        {
            if (list.GetValue(HandlerProperty) is EventHandler previous)
                list.ItemContainerGenerator.StatusChanged -= previous;
            list.ClearValue(HandlerProperty);
            list.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox list) ScrollToEnd(list);
    }

    private static void ScrollToEnd(ListBox list)
    {
        int count = list.Items.Count;
        if (count > 0) list.ScrollIntoView(list.Items[count - 1]);
    }
}
