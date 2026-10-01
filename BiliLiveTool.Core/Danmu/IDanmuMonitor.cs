namespace BiliLiveTool.Core.Danmu;

/// <summary>
/// 弹幕监听控制缝：切账号/登出前先停旧房间监听（指南步骤 5 与 AGENTS
/// 硬规则，对照原门面无条件先 danmu_service.stop）。由 DanmuService 实现。
/// </summary>
public interface IDanmuMonitor
{
    /// <summary>停止监听；未运行时为无操作。失败抛异常，调用方须中止切换。</summary>
    Task StopAsync(CancellationToken ct);
}
