namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// The 手机传文件 capability (手机传文件页). Like <see cref="Netdisk.NetdiskFeature"/> it is a
/// navigation entry rather than a file processor: the page hosts the LAN receiver (QR code +
/// embedded upload web server), and incoming phone files land in the job list as finished
/// records. Nothing is ever routed through <see cref="Run"/>.
/// </summary>
public sealed class LanTransferFeature : IFeature
{
    public string Id => "transfer.page";

    public string DisplayName => "手机传文件";

    public string ProcessingText => "正在接收…";

    /// <summary>No existing file is ever handled by the transfer feature.</summary>
    public bool CanHandle(string filePath) => false;

    public FeatureResult Run(string inputPath, FeatureContext context) =>
        throw new NotSupportedException("手机传文件不处理已有文件；接收由局域网接收服务直接完成。");
}
