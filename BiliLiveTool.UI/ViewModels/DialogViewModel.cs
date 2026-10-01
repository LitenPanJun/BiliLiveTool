using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>统一对话框态：消息框（可带链接）与确认框共用一个槽位。</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly Action _close;

    public string Title { get; }

    public string Message { get; }

    /// <summary>可选链接（人脸验证 60024/60043 的 URL），展示并可复制。</summary>
    public string? Url { get; }

    public bool IsConfirm { get; }

    public Task<bool> Result => _result.Task;

    public DialogViewModel(string title, string message, string? url, bool isConfirm, Action close)
    {
        Title = title;
        Message = message;
        Url = url;
        IsConfirm = isConfirm;
        _close = close;
    }

    [RelayCommand]
    private void Accept()
    {
        if (_result.TrySetResult(true))
            _close();
    }

    [RelayCommand]
    private void Reject()
    {
        if (_result.TrySetResult(false))
            _close();
    }
}
