using System.Net.WebSockets;
using BiliLiveTool.Infrastructure.Bilibili;

namespace BiliLiveTool.Services.Danmu;

/// <summary>ClientWebSocket 的生产实现，请求头复刻原 api.headers 浏览器头。</summary>
internal sealed class ClientWebSocketConnection : IWebSocketConnection
{
    // 对应 data.py header 的文本头（content-type 属内容头，握手 GET 不带）
    private const string AcceptHeader = "application/json, text/plain, */*";
    private const string AcceptLanguageHeader = "zh-CN,zh;q=0.9";

    private readonly ClientWebSocket _socket;

    private ClientWebSocketConnection(ClientWebSocket socket) => _socket = socket;

    public static async Task<IWebSocketConnection> ConnectAsync(Uri url, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("user-agent", BilibiliApiClient.UserAgent);
        socket.Options.SetRequestHeader("accept", AcceptHeader);
        socket.Options.SetRequestHeader("accept-language", AcceptLanguageHeader);
        await socket.ConnectAsync(url, ct).ConfigureAwait(false);
        return new ClientWebSocketConnection(socket);
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct) =>
        await _socket.SendAsync(
            new ReadOnlyMemory<byte>(payload), WebSocketMessageType.Binary, endOfMessage: true, ct)
            .ConfigureAwait(false);

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var frame = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            frame.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return frame.ToArray();
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false);
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
