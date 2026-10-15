using System.Windows;
using System.Windows.Controls;

namespace WatermarkRemover.App.Behaviors;

/// <summary>
/// Attached behavior: focuses the element (selecting text on TextBoxes) whenever it becomes
/// visible. Used by the inline rename box so the user can type immediately after clicking the
/// file name.
/// </summary>
public static class FocusWhenVisible
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(FocusWhenVisible), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        if ((bool)e.NewValue) element.IsVisibleChanged += OnIsVisibleChanged;
        else element.IsVisibleChanged -= OnIsVisibleChanged;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not UIElement element) return;

        element.Dispatcher.BeginInvoke(() =>
        {
            if (!element.IsVisible) return;
            element.Focus();
            if (element is TextBox textBox) textBox.SelectAll();
        });
    }
}
