using BiliLiveTool.Core.Danmu;
using BiliLiveTool.UI.ViewModels;
using FluentAssertions;

namespace BiliLiveTool.Tests.Danmu;

public class DanmuSegmentTests
{
    private static readonly DanmuEmote Hug = new("http://i0.hdslb.com/bfs/live/e607.png", 20, 20);
    private static readonly DanmuEmote Big = new("https://i0.hdslb.com/bfs/live/room.png", 100, 162);

    private static Dictionary<string, DanmuEmote> Emotes() => new()
    {
        ["[捂脸]"] = Hug,
        ["[dog]"] = Big,
        ["基本功"] = Big,
    };

    [Fact]
    public void Build_Without_Emotes_Returns_Null()
    {
        DanmuSegment.Build("[捂脸]", null).Should().BeNull();
        DanmuSegment.Build("[捂脸]", new Dictionary<string, DanmuEmote>()).Should().BeNull();
    }

    [Fact]
    public void Build_Without_Any_Token_Hit_Returns_Null()
    {
        DanmuSegment.Build("普通弹幕", Emotes()).Should().BeNull();
        DanmuSegment.Build("[未知表情]", Emotes()).Should().BeNull();
    }

    [Fact]
    public void Build_Splits_Text_And_Emote_Tokens_In_Order()
    {
        var parts = DanmuSegment.Build("开播[捂脸][dog]啦", Emotes());

        parts.Should().NotBeNull();
        parts!.Should().HaveCount(4);
        parts[0].IsImage.Should().BeFalse();
        parts[0].Text.Should().Be("开播");
        parts[1].IsImage.Should().BeTrue();
        parts[1].Text.Should().Be("[捂脸]");
        parts[1].Width.Should().Be(20);
        parts[1].ImageUrl.Should().StartWith("https://"); // http CDN 归一为 https
        parts[2].Text.Should().Be("[dog]");
        parts[2].Width.Should().Be(44); // 100×162 等比收敛到最大边 72
        parts[2].Height.Should().Be(72);
        parts[3].IsImage.Should().BeFalse();
        parts[3].Text.Should().Be("啦");
    }

    [Fact]
    public void Build_Empty_Text_Segment_Keeps_Text_Degrade_Before_Load()
    {
        var parts = DanmuSegment.Build("[捂脸]", Emotes());

        parts.Should().HaveCount(1);
        parts![0].IsImage.Should().BeTrue();
        parts[0].ShowText.Should().BeTrue(); // 懒加载：未绑定不触发下载，先显示 token
        parts[0].ShowImage.Should().BeFalse();
    }

    [Fact]
    public void Build_Whole_Message_Hit_Yields_Single_Image_Segment()
    {
        // 单表情弹幕（dm_type=1 装扮表情）：整条内容作 key
        var parts = DanmuSegment.Build("基本功", Emotes());

        parts.Should().HaveCount(1);
        parts![0].IsImage.Should().BeTrue();
        parts[0].Text.Should().Be("基本功");
        parts[0].Width.Should().Be(44);
    }

    [Fact]
    public void Text_Segment_Never_Images()
    {
        var seg = DanmuSegment.FromText("hello");

        seg.IsImage.Should().BeFalse();
        seg.ImageUrl.Should().BeNull();
        seg.ShowText.Should().BeTrue();
        seg.ShowImage.Should().BeFalse();
        seg.Bitmap.Should().BeNull(); // 文本段读取不触发下载
    }

    // --- 不丢失不变式：片段 Text 首尾相接 == 原消息（图片段 Text 即 token） ---

    private static string Concat(IReadOnlyList<DanmuSegment> parts) =>
        string.Concat(parts.Select(p => p.Text));

    [Fact]
    public void Build_Mixed_Text_And_Multiple_Emotes_Restores_Original()
    {
        const string msg = "开播啦[捂脸]中段[dog]结束[捂脸]";

        var parts = DanmuSegment.Build(msg, Emotes());

        parts.Should().NotBeNull();
        Concat(parts!).Should().Be(msg);
    }

    [Fact]
    public void Build_Unknown_Token_And_Emoji_In_Text_Are_Not_Lost()
    {
        // 生僻/未收录 token 保留在文本段；😀 为代理对，切分不得伤及码点
        const string msg = "前段[未知表情]😀中段[捂脸]";

        var parts = DanmuSegment.Build(msg, Emotes());

        parts.Should().NotBeNull();
        Concat(parts!).Should().Be(msg);
        parts![0].Text.Should().Be("前段[未知表情]😀中段");
    }

    [Fact]
    public void Build_Zwj_Emoji_Sequence_Not_Split_By_Token_Boundary()
    {
        const string msg = "👨‍👩‍👧‍👦[捂脸]尾巴";

        var parts = DanmuSegment.Build(msg, Emotes());

        parts.Should().NotBeNull();
        Concat(parts!).Should().Be(msg); // ZWJ 拼合 emoji 整体留在文本段
        parts![0].Text.Should().Be("👨‍👩‍👧‍👦");
    }

    [Fact]
    public void Build_Token_At_Start_And_Adjacent_Tokens_Restore_Original()
    {
        const string msg = "[捂脸][捂脸]头[未知]尾[dog]";

        var parts = DanmuSegment.Build(msg, Emotes());

        parts.Should().NotBeNull();
        Concat(parts!).Should().Be(msg);
    }

    [Fact]
    public void Build_Unclosed_Bracket_Falls_Back_To_Full_Text()
    {
        const string msg = "残缺[捂脸没有右括号";

        DanmuSegment.Build(msg, Emotes()).Should().BeNull(); // 整行走纯文本，原文不丢
    }
}
