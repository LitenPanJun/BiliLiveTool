using System.Threading.Channels;
using BiliLiveTool.Services.Danmu;

namespace BiliLiveTool.Tests.Fakes;

/// <summary>假 WebSocket：记录发出的帧，入队驱动接收，可模拟关闭与故障。</summary>
internal sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<object?> _inbox = Channel.CreateUnbounded<object?>();

    public List<byte[]> SentPackets { get; } = [];

    public bool Closed { get; private set; }

    public void Enqueue(byte[] data) => _inbox.Writer.TryWrite(data);

    public void EnqueueClosed() => _inbox.Writer.TryWrite(null);

    public Task SendAsync(byte[] payload, CancellationToken ct)
    {
        SentPackets.Add(payload);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var item = await _inbox.Reader.ReadAsync(ct).ConfigureAwait(false);
        if (item is Exception error)
            throw error;
        return item as byte[];
    }

    public Task CloseAsync(CancellationToken ct)
    {
        Closed = true;
        return Task.CompletedTask;
    }
}
