using Avalonia;

namespace BiliLiveTool.App;

/// <summary>Avalonia 启动入口，对照原 main.py 的窗口与生命周期职责。</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
