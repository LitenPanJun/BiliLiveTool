namespace BiliLiveTool.Services.Danmu;

/// <summary>
/// WebSocket 连接缝：隔离 ClientWebSocket，便于以假连接驱动收发与重连测试。
/// </summary>
internal interface IWebSocketConnection
{
    /// <summary>发送一帧二进制数据。</summary>
    Task SendAsync(byte[] payload, CancellationToken ct);

    /// <summary>接收一帧完整二进制消息；对端关闭返回 null，传输故障抛异常。</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);

    /// <summary>发起关闭握手并释放底层资源；容忍故障静默返回。</summary>
    Task CloseAsync(CancellationToken ct);
}
