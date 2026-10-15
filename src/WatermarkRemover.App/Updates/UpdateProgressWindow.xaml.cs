using System.Threading;
using System.Windows;

namespace WatermarkRemover.App.Updates;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
        Cts = new CancellationTokenSource();
        Closed += (_, _) => Cts.Cancel(); // closing the window (X button) cancels the download
    }

    public CancellationTokenSource Cts { get; }

    /// <summary>Thread-safe progress update; download runs on a worker context.</summary>
    public void Report(double percent) => Dispatcher.Invoke(() =>
    {
        Bar.Value = percent;
        PercentText.Text = $"{percent:F0}%";
    });

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
