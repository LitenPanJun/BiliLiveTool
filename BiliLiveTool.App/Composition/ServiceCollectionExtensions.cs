using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Config;
using BiliLiveTool.Core.Danmu;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Infrastructure.Security;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Config;
using BiliLiveTool.Services.Danmu;
using BiliLiveTool.Services.Live;
using BiliLiveTool.Services.User;
using BiliLiveTool.UI.Logging;
using BiliLiveTool.UI.Services;
using BiliLiveTool.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.App.Composition;

/// <summary>
/// 应用组合根：全部服务经构造函数注入装配（指南步骤示例的 AddSingleton /
/// AddHttpClient 形态），日志汇入掩码后的 UI 控制台并落盘 logs/app.log。
/// </summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBiliLiveTool(this IServiceCollection services)
    {
        // 安全基线：日志唯一出口 + config.json 仅非密钥 + 机密走系统钥匙串
        services.AddSingleton<ISecretMasker, SecretMasker>();
        services.AddSingleton<FileConfigStore>();
        services.AddSingleton<IAccountStore>(sp => sp.GetRequiredService<FileConfigStore>());
        services.AddSingleton<IAppConfigStore>(sp => sp.GetRequiredService<FileConfigStore>());
        services.AddSingleton<KeyringSecretVault>();
        services.AddSingleton<ISecretVault>(sp => sp.GetRequiredService<KeyringSecretVault>());

        // 日志：唯一提供者为 UiLogSink（写入即脱敏），最低级别 Debug
        // 以保未知弹幕 cmd 的 debug 记录可见
        services.AddSingleton<UiLogSink>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<UiLogSink>());
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        // 会话与服务层
        services.AddSingleton<AuthSessionStore>();
        // API 客户端必须全服务共享单实例：AddHttpClient<,> 是瞬态注册，曾导致
        // Auth/User/Live/Danmu 各持独立 cookie jar——登录与启动恢复只写进各自
        // 实例，重启后 Live/Danmu/Auth 的请求裸奔（-101、弹幕鉴权被拒），
        // 全靠进程内 CookieContainer 掩盖、重启即现形。原版亦为单一 api 对象。
        services.AddHttpClient(BilibiliApiClient.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Cookie 唯一来源是 _cookies 手工头（对齐原版只发自身字典），
                // 关闭容器避免第二真相源随重启/回收丢失与重复 Cookie 头
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddSingleton<IBilibiliApiClient>(sp => new BilibiliApiClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(BilibiliApiClient.HttpClientName)));
        services.AddSingleton<UserService>();
        services.AddSingleton<LiveService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<DanmuService>();
        services.AddSingleton<IDanmuMonitor>(sp => sp.GetRequiredService<DanmuService>());

        // UI：DialogHost 以具体类型供窗口绑定，接口供子 VM 注入
        services.AddSingleton<DialogHost>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogHost>());
        services.AddSingleton<ClipboardHub>();
        services.AddSingleton<ConsoleViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<LiveViewModel>();
        services.AddSingleton<DanmuViewModel>();
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
