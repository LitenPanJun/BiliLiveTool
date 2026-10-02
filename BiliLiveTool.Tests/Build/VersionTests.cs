using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using BiliLiveTool.Core.Danmu;
using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Services.Danmu;
using BiliLiveTool.UI.ViewModels;
using FluentAssertions;

namespace BiliLiveTool.Tests.Build;

/// <summary>
/// 版本单源（根目录 VERSION → Directory.Build.props 派生）与程序集详细信息的全局一致性：
/// 升版本只改 VERSION 一行，此处封死格式违约与各工程版本漂移（不硬编码版本号本身）。
/// </summary>
public sealed class VersionTests
{
    /// <summary>VERSION 单源标签，如 0.2.0.261002（yymmdd 对应提交日）。</summary>
    private static readonly string Label = ReadVersionLabel();

    /// <summary>派生的程序集/File 版本：yymmdd 段超 16 位（65535）截去后补 0。</summary>
    private static readonly string Derived = Label[..Label.LastIndexOf('.')] + ".0";

    private static string ReadVersionLabel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
            dir = dir.Parent;
        dir.Should().NotBeNull("VERSION 文件应位于仓库根目录");
        return File.ReadAllText(Path.Combine(dir!.FullName, "VERSION")).Trim();
    }

    private static Assembly[] Assemblies() =>
    [
        typeof(BiliLiveTool.App.App).Assembly, // 输出可执行文件 BiliLiveTool.dll
        typeof(DanmuEvent).Assembly,
        typeof(BilibiliApiClient).Assembly,
        typeof(DanmuService).Assembly,
        typeof(DanmuViewModel).Assembly,
    ];

    [Fact]
    public void Version_Label_Follows_Major_Minor_Patch_Yymmdd_Convention()
    {
        Regex.IsMatch(Label, @"^\d+\.\d+\.\d+\.\d{6}$")
            .Should().BeTrue($"VERSION 内容须为 major.minor.patch.yymmdd，实际为 {Label}");
    }

    [Fact]
    public void All_Assemblies_Share_Derived_Assembly_And_File_Version()
    {
        foreach (var asm in Assemblies())
        {
            asm.GetName().Version!.ToString().Should().Be(Derived, because: asm.GetName().Name);
        }
    }

    [Fact]
    public void File_Version_Info_Carries_Full_Label_And_Product_Details()
    {
        foreach (var asm in Assemblies())
        {
            var info = FileVersionInfo.GetVersionInfo(asm.Location);
            var name = asm.GetName().Name;

            info.FileVersion.Should().Be(Derived, because: name);
            info.ProductVersion.Should().StartWith(Label, because: "完整版本（可含 +git sha）应可见");
            info.CompanyName.Should().Be("BiliLiveTool", because: name);
            info.ProductName.Should().Be("BiliLiveTool", because: name);
        }
    }

    [Fact]
    public void Informational_Version_Starts_With_Version_Label()
    {
        foreach (var asm in Assemblies())
        {
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            informational.Should().NotBeNull(because: asm.GetName().Name);
            informational!.InformationalVersion.Should().StartWith(Label, because: asm.GetName().Name);
        }
    }
}
