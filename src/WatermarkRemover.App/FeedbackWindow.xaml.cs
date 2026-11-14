using System;
using System.Threading.Tasks;
using System.Windows;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App;

/// <summary>
/// 随手反馈窗口：一个文本框 + 提交。从某条识别记录进来时带上该次操作号（日志切片按号
/// 切），从全局入口进来时只附最近日志尾部。提交过程见 <see cref="FeedbackService"/>。
/// </summary>
public partial class FeedbackWindow : Window
{
    private readonly FeedbackService _service;
    private readonly string? _operationId;
    private bool _submitting;

    public FeedbackWindow(AppSettings settings, ISecretProtector protector,
        string? operationId, string? contextText)
    {
        InitializeComponent();
        _service = new FeedbackService(settings, protector);
        _operationId = operationId;
        if (!string.IsNullOrWhiteSpace(contextText))
        {
            ContextText.Text = $"关联操作：{contextText}";
            ContextText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => DescriptionBox.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 提交中不让关窗：否则续行会落到已关闭的窗口上。
        if (_submitting) return;
        Close();
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        if (_submitting) return;
        if (string.IsNullOrWhiteSpace(DescriptionBox.Text))
        {
            StatusText.Text = "请先输入问题描述。";
            DescriptionBox.Focus();
            return;
        }

        _submitting = true;
        SubmitButton.IsEnabled = false;
        StatusText.Text = "提交中…";
        try
        {
            FeedbackResult result = await _service.SubmitAsync(
                _operationId, null, DescriptionBox.Text, ContactBox.Text);
            StatusText.Text = result.Message;
            if (result.Ok)
            {
                // 让用户看清「已提交」再关窗。
                await Task.Delay(700);
                DialogResult = true;
                Close();
            }
        }
        finally
        {
            _submitting = false;
            SubmitButton.IsEnabled = true;
        }
    }
}
