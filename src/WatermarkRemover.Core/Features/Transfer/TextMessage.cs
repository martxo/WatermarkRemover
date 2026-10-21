namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// One text message posted from the phone, carrying the metadata needed to present it
/// chat-style on the desktop. Immutable payload of <see cref="LanTransferServer.TextReceived"/>.
/// </summary>
/// <param name="Text">The exact text the phone posted.</param>
/// <param name="SenderLabel">Short sender description derived from the browser UA plus the IP
/// tail (e.g. 「iPhone · …31」) — enough to tell two phones apart, nothing more invasive.</param>
/// <param name="ReceivedAt">Server-side receive time; the phone's clock is not trusted.</param>
public sealed record TextMessage(string Text, string SenderLabel, DateTime ReceivedAt);
