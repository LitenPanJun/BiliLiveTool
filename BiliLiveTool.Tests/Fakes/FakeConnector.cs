using BiliLiveTool.Services.Danmu;

namespace BiliLiveTool.Tests.Fakes;

/// <summary>假连接器：记录请求的 URL，可注入前 N 次连接失败以驱动重连与熔断。</summary>
internal sealed class FakeConnector
{
    public List<Uri> Urls { get; } = [];

    public List<FakeWebSocketConnection> Sockets { get; } = [];

    public int FailNext { get; set; }

    public Task<IWebSocketConnection> ConnectAsync(Uri url, CancellationToken ct)
    {
        Urls.Add(url);
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("connect refused");
        }

        var socket = new FakeWebSocketConnection();
        Sockets.Add(socket);
        return Task.FromResult<IWebSocketConnection>(socket);
    }
}
