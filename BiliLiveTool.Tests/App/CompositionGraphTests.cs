using BiliLiveTool.App.Composition;
using BiliLiveTool.Core.Bilibili;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BiliLiveTool.Tests.App;

/// <summary>
/// 组合根依赖图：启动装配全图可解析（曾因 IDanmuMonitor 漏注册，
/// 应用启动以未处理异常 exit 134 裸崩——此测试封死该类回归）。
/// </summary>
public sealed class CompositionGraphTests
{
    [Fact]
    public void Service_Graph_Validates_With_ValidateOnBuild()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddBiliLiveTool()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        provider.Should().NotBeNull();
    }

    [Fact]
    public void Api_Client_Shared_Singleton_Across_All_Services()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddBiliLiveTool()
            .BuildServiceProvider();

        // cookie jar 唯一真相：任何解析点都必须拿到同一实例。
        // 曾因 AddHttpClient<,> 瞬态注册使 4 个服务各持独立 _cookies——
        // 重启后 Live/Danmu/Auth 裸奔 -101，此测试封死回归。
        var first = provider.GetRequiredService<IBilibiliApiClient>();
        var second = provider.GetRequiredService<IBilibiliApiClient>();

        first.Should().BeSameAs(second);
    }
}
