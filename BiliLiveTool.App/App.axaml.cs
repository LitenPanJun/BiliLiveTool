using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using BiliLiveTool.App.Composition;
using BiliLiveTool.Core.Config;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Danmu;
using BiliLiveTool.Services.Live;
using BiliLiveTool.UI.Services;
using BiliLiveTool.UI.ViewModels;
using BiliLiveTool.UI.Views;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.App;

/// <summary>
/// 应用根：组合根装配、窗口接线、托盘与退出生命周期（对照原 main.py）。
/// 退出规则：关窗按 min_to_tray 隐藏或退出；托盘退出统一清理，
/// 2 秒预算内先停播再停弹幕，保证 3 秒内无残留进程。
/// </summary>
public partial class App : Application
{
    private static readonly Uri IconUri = new("avares://BiliLiveTool.App/Assets/app.ico");

    private ServiceProvider? _provider;
    private MainWindow? _window;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _provider = new ServiceCollection()
                .AddBiliLiveTool()
                .BuildServiceProvider();

            var main = _provider.GetRequiredService<MainViewModel>();
            _window = new MainWindow { DataContext = main };
            desktop.MainWindow = _window;

            _window.Opened += (_, _) => OnOpenedAsync(_window, main);
            _window.Closing += (_, e) =>
            {
                // 对照原 window_close：min_to_tray 时隐藏而非退出
                if (!_exiting && _provider?.GetRequiredService<IAppConfigStore>().MinToTray == true)
                {
                    e.Cancel = true;
                    _window.Hide();
                }
            };
            desktop.Exit += (_, _) => OnExit();

            SetupTray();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray()
    {
        using var iconStream = AssetLoader.Open(IconUri);
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("显示主窗口") { Command = new RelayCommand(ShowWindow) });
        menu.Items.Add(new NativeMenuItem("退出") { Command = new RelayCommand(ExitApp) });

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "BiliLiveTool",
            Menu = menu,
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (_exiting)
            return;

        _exiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void OnExit()
    {
        try
        {
            // 同步等待但内部限时 2 秒：满足“退出 3 秒内干净注销”
            ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退出路径吞掉一切异常，确保进程退出
        }
        finally
        {
            _provider?.Dispose();
        }
    }

    private async Task ShutdownAsync()
    {
        if (_provider is null)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var log = _provider.GetService<ILogger<App>>();
        try
        {
            // 对照原 window_close 的清理序：先停播，再停弹幕
            if (_provider.GetRequiredService<AuthSessionStore>().CurrentSnapshot.IsLive)
                await _provider.GetRequiredService<LiveService>().StopLiveAsync(cts.Token)
                    .ConfigureAwait(false);

            await _provider.GetRequiredService<DanmuService>().StopAsync(cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log?.LogWarning(e, "Shutdown cleanup failed");
        }
        finally
        {
            var main = _provider.GetService<MainViewModel>();
            main?.Console.Stop();
            main?.Danmu.StopPump();
        }
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
