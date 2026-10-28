using System.Windows;

namespace WatermarkRemover.App;

/// <summary>
/// Small target-folder prompt for 网盘管理 → 移动. The view model receives the typed remote path
/// and creates missing folders through the shared INetdiskSession path helpers.
/// </summary>
public partial class NetdiskMoveDialog : Window
{
    public NetdiskMoveDialog(string currentPath)
    {
        InitializeComponent();
        TargetBox.Text = currentPath;
        Loaded += (_, _) =>
        {
            TargetBox.Focus();
            TargetBox.SelectAll();
        };
    }

    /// <summary>Typed target, filled only when the user confirms.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>Shows the dialog and returns the target path, or null when cancelled.</summary>
    public static string? Prompt(string currentPath)
    {
        var dialog = new NetdiskMoveDialog(currentPath);
        return dialog.ShowDialog() == true ? dialog.TargetPath : null;
    }

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        string target = TargetBox.Text.Trim();
        if (target.Length == 0)
        {
            MessageBox.Show(this, "请输入目标目录，例如 /上传 或 /来自分享/2026。", "移动到",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TargetPath = target;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
