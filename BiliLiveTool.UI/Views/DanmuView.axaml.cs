using Avalonia.Controls;
using Avalonia.Input;
using BiliLiveTool.UI.ViewModels;

namespace BiliLiveTool.UI.Views;

/// <summary>弹幕页视图（消息列表 + 发送栏）。</summary>
public partial class DanmuView : UserControl
{
    public DanmuView()
    {
        InitializeComponent();
    }

    private async void OnSendKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (DataContext is DanmuViewModel { SendCommand: { } command })
            await command.ExecuteAsync(null);
    }
}
