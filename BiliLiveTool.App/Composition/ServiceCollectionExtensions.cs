using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Config;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Infrastructure.Bilibili;
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
/// AddHttpClient 形态），日志仅汇入掩码后的 UI 控制台。
/// </summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBiliLiveTool(this IServiceCollection services)
    {
        // 安全基线：日志唯一出口 + 机密/账号存储（config.json 仅非密钥，
        // 机密后端随后提交切换为系统钥匙串）
        services.AddSingleton<ISecretMasker, SecretMasker>();
        services.AddSingleton<FileConfigStore>();
        services.AddSingleton<IAccountStore>(sp => sp.GetRequiredService<FileConfigStore>());
        services.AddSingleton<IAppConfigStore>(sp => sp.GetRequiredService<FileConfigStore>());
        services.AddSingleton<ISecretVault, InMemorySecretVault>();

        // 日志：唯一提供者为 UiLogSink（写入即脱敏），最低级别 Debug
        // 以保未知弹幕 cmd 的 debug 记录可见
        services.AddSingleton<UiLogSink>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<UiLogSink>());
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        // 会话与服务层
        services.AddSingleton<AuthSessionStore>();
        services.AddHttpClient<IBilibiliApiClient, BilibiliApiClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<UserService>();
        services.AddSingleton<LiveService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<DanmuService>();

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
