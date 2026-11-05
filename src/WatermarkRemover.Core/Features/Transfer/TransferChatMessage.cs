namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>Who sent a chat row: the phone browser or this computer.</summary>
public enum TransferChatDirection
{
    Phone,
    Computer,
}

/// <summary>What a chat row carries: plain text, a generic file, an image, or a share link.</summary>
public enum TransferChatKind
{
    Text,
    File,
    Image,
    /// <summary>A 扫描全能王-style share link; its row tracks the download chain's progress.</summary>
    Link,
}

/// <summary>
/// One row in the 手机传文件 chat. Shared by the phone page's polling endpoint and the computer
/// side's message list, so both ends render the same ordered conversation.
/// </summary>
public sealed record TransferChatMessage(
    int Id,
    TransferChatDirection Direction,
    TransferChatKind Kind,
    string Text,
    string FileName,
    long Size,
    DateTime CreatedAt)
{
    public bool IsFile => Kind is TransferChatKind.File or TransferChatKind.Image;
}
