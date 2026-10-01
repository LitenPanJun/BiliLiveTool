using BiliLiveTool.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliLiveTool.UI.Services;

/// <summary>
/// 对话框宿主：持有唯一 Dialog 槽位（对照原 MessageModal 单实例），
/// 并发请求排队串行展示，窗口绑定 DialogHost.Current。
/// </summary>
public sealed partial class DialogHost : ObservableObject, IDialogService
{
    [ObservableProperty]
    private DialogViewModel? _current;

    public async Task ShowMessageAsync(string title, string message, string? url = null)
    {
        await WaitForFreeAsync().ConfigureAwait(true);
        var dialog = new DialogViewModel(title, message, url, isConfirm: false, Clear);
        Current = dialog;
        await dialog.Result.ConfigureAwait(true);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        await WaitForFreeAsync().ConfigureAwait(true);
        var dialog = new DialogViewModel(title, message, null, isConfirm: true, Clear);
        Current = dialog;
        return await dialog.Result.ConfigureAwait(true);
    }

    private void Clear() => Current = null;

    private async Task WaitForFreeAsync()
    {
        while (Current is { } pending)
            await pending.Result.ConfigureAwait(true);
    }
}
