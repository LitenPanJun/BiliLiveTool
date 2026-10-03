using BiliLiveTool.Core.Danmu;
using BiliLiveTool.UI.ViewModels;
using FluentAssertions;

namespace BiliLiveTool.Tests.Danmu;

/// <summary>
/// 弹幕行渲染：含表情的行走行内片段模板（纯文本 Display 被隐藏），
/// 发送人前缀必须并入片段首段——否则表情弹幕只见内容不见发送人。
/// </summary>
public class DanmuViewModelFormatTests
{
    private static readonly DanmuEmote Hug = new("https://i0.hdslb.com/bfs/live/e607.png", 20, 20);

    private static Dictionary<string, DanmuEmote> Emotes() => new()
    {
        ["[捂脸]"] = Hug,
        ["基本功"] = Hug,
    };

    [Fact]
    public void Format_Emote_Danmu_Prepends_Sender_As_First_Segment()
    {
        var evt = new DanmuEvent(
            DanmuEventTypes.Danmu, "开播[捂脸]", 5, "tester", Emotes: Emotes());

        var (display, parts) = DanmuViewModel.Format(evt);

        display.Should().Be("tester: 开播[捂脸]"); // 复制/检索用纯文本仍带发送人
        parts.Should().NotBeNull();
        parts![0].IsImage.Should().BeFalse();
        parts[0].Text.Should().Be("tester: "); // 行内片段首段即发送人
        parts[1].Text.Should().Be("开播");
        parts[2].IsImage.Should().BeTrue();
        parts[2].Text.Should().Be("[捂脸]");
    }

    [Fact]
    public void Format_Single_Emote_Danmu_Still_Shows_Sender()
    {
        // 单表情弹幕（dm_type=1）：整条内容命中，仅产出一个图片段，
        // 若不补发送人前缀则整行只有图
        var evt = new DanmuEvent(
            DanmuEventTypes.Danmu, "基本功", 9, "sender", Emotes: Emotes());

        var (_, parts) = DanmuViewModel.Format(evt);

        parts.Should().HaveCount(2);
        parts![0].Text.Should().Be("sender: ");
        parts[1].IsImage.Should().BeTrue();
        parts[1].Text.Should().Be("基本功");
    }

    [Fact]
    public void Format_Danmu_Without_Emotes_Keeps_Null_Parts_And_Display_Text()
    {
        var evt = new DanmuEvent(DanmuEventTypes.Danmu, "普通弹幕", 1, "u");

        var (display, parts) = DanmuViewModel.Format(evt);

        display.Should().Be("u: 普通弹幕");
        parts.Should().BeNull(); // 无表情：走纯文本模板，发送人由 Display 承载
    }

    [Fact]
    public void Format_Emote_Danmu_Without_Sender_Omits_Prefix_Segment()
    {
        var evt = new DanmuEvent(DanmuEventTypes.Danmu, "[捂脸]", Emotes: Emotes());

        var (_, parts) = DanmuViewModel.Format(evt);

        parts.Should().HaveCount(1);
        parts![0].IsImage.Should().BeTrue(); // 无发送人不注入空文本段
    }
}
