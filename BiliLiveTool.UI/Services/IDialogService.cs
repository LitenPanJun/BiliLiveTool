namespace BiliLiveTool.UI.Services;

/// <summary>
/// 对话框服务缝：MainViewModel 与各子 ViewModel 经此弹统一 Dialog，
/// 避免子 VM 反向持有主 VM 造成循环依赖。
/// </summary>
public interface IDialogService
{
    /// <summary>展示消息框（可附带链接，如人脸验证 URL），用户确认后完成。</summary>
    Task ShowMessageAsync(string title, string message, string? url = null);

    /// <summary>展示确认框；确认返回 true，取消返回 false。</summary>
    Task<bool> ConfirmAsync(string title, string message);
}
