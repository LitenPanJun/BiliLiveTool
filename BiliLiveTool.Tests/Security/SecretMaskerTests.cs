using BiliLiveTool.Infrastructure.Bilibili;
using FluentAssertions;

namespace BiliLiveTool.Tests.Security;

public class SecretMaskerTests
{
    private readonly SecretMasker _masker = new();

    [Theory]
    [InlineData("rtmp://live.example.com/xyz123456789", "rtmp*****6789")] // 长度 > 8：前4+5星+后4
    [InlineData("short", "*****")]                                       // 长度 <= 8：按位补 *
    [InlineData("12345678", "********")]                                 // 恰好等于 4+4
    [InlineData("", "")]
    public void MaskString_Follows_Reference_Layout(string input, string expected) =>
        _masker.MaskString(input).Should().Be(expected);

    [Fact]
    public void MaskString_Null_Returns_Empty() =>
        _masker.MaskString(null).Should().Be(""); // util.mask_string: not s → ""

    [Fact]
    public void MaskString_Custom_Widths_Match_Reference()
    {
        _masker.MaskString("1234567890", 2, 2).Should().Be("12*****90");
        _masker.MaskString("42", 2, 2).Should().Be("**"); // len <= start+end：按位补 *
    }

    [Fact]
    public void MaskData_Masks_Sensitive_String_And_Number_Recursively()
    {
        const string input = """
            {"rtmp":{"addr":"rtmp://a.b/live/xyz123456789","code":"STREAMCODE123"},
             "uid":1234567890,"title":"开播啦","tags":["a","b"],"note":null}
            """;

        var masked = System.Text.Json.Nodes.JsonNode.Parse(_masker.MaskData(input))!.AsObject();

        masked["rtmp"]!["addr"]!.GetValue<string>().Should().Be("rtmp*****6789");
        masked["rtmp"]!["code"]!.GetValue<string>().Should().Be("STRE*****E123");
        masked["uid"]!.GetValue<string>().Should().Be("12*****90");
        masked["title"]!.GetValue<string>().Should().Be("开播啦");
        masked["tags"]!.ToJsonString().Should().Be("""["a","b"]""");
        masked["note"].Should().BeNull();
    }

    [Fact]
    public void MaskData_Keeps_Non_String_Non_Number_Under_Sensitive_Key()
    {
        // 与原实现一致：敏感键下的布尔/数组按 else 分支原样保留
        var masked = System.Text.Json.Nodes.JsonNode.Parse(
            _masker.MaskData("""{"room_id":[1,2],"key":true}"""))!.AsObject();

        masked["room_id"]!.ToJsonString().Should().Be("[1,2]");
        masked["key"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void MaskData_Invalid_Json_Returns_Parse_Error() =>
        _masker.MaskData("not a json").Should().Be("Parse Error");

    [Fact]
    public void MaskUrl_Masks_Sensitive_Query_And_Quotes_Stars()
    {
        // 与 Python urlencode(doseq=True) 一致：掩码星号被转义为 %2A
        _masker.MaskUrl("https://api.test/x?uid=1234567890&key=abcdef&clean=1")
            .Should().Be("https://api.test/x?uid=12%2A%2A%2A%2A%2A90&key=ab%2A%2A%2A%2A%2Aef&clean=1");
    }

    [Fact]
    public void MaskUrl_Drops_Flag_And_Blank_Params_When_Changed()
    {
        // 复刻 parse_qs：无 '=' 项与空值项不入表，命中敏感键重建时被丢弃
        _masker.MaskUrl("https://api.test/x?uid=1234567890&flag&empty=")
            .Should().Be("https://api.test/x?uid=12%2A%2A%2A%2A%2A90");
        // 未命中敏感键时原串原样返回，被丢弃项不受影响
        _masker.MaskUrl("https://api.test/x?flag&empty=&clean=1")
            .Should().Be("https://api.test/x?flag&empty=&clean=1");
    }

    [Fact]
    public void MaskUrl_Keeps_Fragment()
    {
        _masker.MaskUrl("https://api.test/x?uid=1234567890#frag")
            .Should().Be("https://api.test/x?uid=12%2A%2A%2A%2A%2A90#frag");
    }

    [Fact]
    public void MaskUrl_Untouched_Cases()
    {
        _masker.MaskUrl("https://api.test/x").Should().Be("https://api.test/x");
        _masker.MaskUrl("https://api.test/x?clean=1").Should().Be("https://api.test/x?clean=1");
        _masker.MaskUrl("").Should().Be("");
    }
}
