using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BiliLiveTool.App.Composition;
using BiliLiveTool.UI.Services;
using BiliLiveTool.UI.ViewModels;
using BiliLiveTool.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.App;

/// <summary>应用根：组合根装配、窗口接线、托盘与退出生命周期。</summary>
public partial class App : Application
{
    private ServiceProvider? _provider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _provider = new ServiceCollection()
                .AddBiliLiveTool()
                .BuildServiceProvider();

            var main = _provider.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;

            window.Opened += (_, _) => OnOpenedAsync(window, main);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnOpenedAsync(MainWindow window, MainViewModel main)
    {
        try
        {
            // 剪贴板只能从 TopLevel 获取，窗口打开后注入
            if (_provider?.GetService<ClipboardHub>() is { } hub)
                hub.Backend = window.Clipboard;

            await main.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            _provider?.GetService<ILogger<App>>()?.LogError(e, "Startup init failed");
        }
    }
}
