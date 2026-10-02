using BiliLiveTool.App.Composition;
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
}
