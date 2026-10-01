namespace BiliLiveTool.Services.Danmu;

/// <summary>
/// 弹幕连接时序参数。默认值对照原 danmu_service.py：心跳 30 秒、
/// 退避 5 秒起指数至 60 秒封顶；jitter 与熔断按重构指南增补（原实现无）。
/// </summary>
public sealed record DanmuOptions
{
    /// <summary>心跳间隔（原 30 秒，发送 op=2 后等待）。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>首次重连退避基数（原 5 秒）。</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>重连退避上限（原 60 秒）。</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>退避抖动比例（指南增补，±该比例均匀抖动）。</summary>
    public double JitterRatio { get; init; } = 0.2;

    /// <summary>连续重连失败熔断阈值（指南增补，原实现无次数上限）。</summary>
    public int BreakerThreshold { get; init; } = 10;
}
