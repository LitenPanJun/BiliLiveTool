using BiliLiveTool.Core.Config;

namespace BiliLiveTool.Services.Config;

/// <summary>进程内应用偏好（对照原 config_manager.data 字段），落盘后端随 P2 替换。</summary>
public sealed class InMemoryAppConfigStore : IAppConfigStore
{
    private volatile bool _minToTray = true;

    public bool MinToTray => _minToTray;

    public void SetMinToTray(bool value) => _minToTray = value;
}
