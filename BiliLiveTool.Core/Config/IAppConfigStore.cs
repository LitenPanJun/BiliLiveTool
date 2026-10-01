namespace BiliLiveTool.Core.Config;

/// <summary>
/// 应用偏好存储缝（对照原 config_manager 的非密钥字段）；
/// P1 为进程内实现，config.json 落盘后端随 P2 落地。
/// </summary>
public interface IAppConfigStore
{
    /// <summary>关窗最小化到托盘（对照原 min_to_tray，默认 true）。</summary>
    bool MinToTray { get; }

    /// <summary>更新偏好（落盘后端在此持久化）。</summary>
    void SetMinToTray(bool value);
}
