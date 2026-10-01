using Avalonia.Controls;
using Avalonia.Interactivity;
using BiliLiveTool.UI.ViewModels;

namespace BiliLiveTool.UI.Views;

/// <summary>统一对话框视图；复制走 TopLevel 剪贴板（视图层职责）。</summary>
public partial class DialogView : UserControl
{
    public DialogView()
    {
        InitializeComponent();
    }

    private async void OnCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DialogViewModel { Url: { } url })
            return;

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(url);
        }
        catch (Exception)
        {
            // 剪贴板瞬时占用：对话框内不额外弹错
        }
    }
}
