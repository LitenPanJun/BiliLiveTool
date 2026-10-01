using BiliLiveTool.Core.Danmu;

namespace BiliLiveTool.Tests.Fakes;

/// <summary>弹幕监听假件：记录停止次数，可注入失败以验证切换中止。</summary>
internal sealed class FakeDanmuMonitor : IDanmuMonitor
{
    public int StopCount { get; private set; }

    public Exception? FailWith { get; set; }

    public Task StopAsync(CancellationToken ct)
    {
        StopCount++;
        if (FailWith is { } error) throw error;
        return Task.CompletedTask;
    }
}
