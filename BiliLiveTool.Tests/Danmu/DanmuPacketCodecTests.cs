using System.Text.Json.Nodes;
using BiliLiveTool.Infrastructure.Danmu;
using BiliLiveTool.Tests.Canary;
using FluentAssertions;

namespace BiliLiveTool.Tests.Danmu;

public class DanmuPacketCodecTests
{
    private readonly DanmuPacketCodec _codec = new();

    private static JsonObject Packets => CanaryVectors.Root["packets"]!.AsObject();

    [Fact]
    public void Encode_Matches_Python_Struct_Pack_Bytes()
    {
        foreach (var item in Packets["encode"]!.AsArray())
        {
            var op = item!["op"]!.GetValue<int>();
            var body = item["body"]!.GetValue<string>();
            var expected = Convert.FromHexString(item["expectedHex"]!.GetValue<string>());

            _codec.Encode(op, body).Should().Equal(expected, $"op={op}");
        }
    }

    [Fact]
    public void Decode_Reproduces_Python_Built_Frames()
    {
        foreach (var (name, node) in Packets["decode"]!.AsObject())
        {
            var data = Convert.FromHexString(node!["hex"]!.GetValue<string>());
            var result = _codec.Decode(data);

            var expected = node["frames"]!.AsArray()
                .Select(frame => (
                    Op: frame!["op"]!.GetValue<int>(),
                    Body: Convert.FromHexString(frame["bodyHex"]!.GetValue<string>())))
                .ToList();

            result.Frames.Should().HaveCount(expected.Count, because: name);
            for (var i = 0; i < expected.Count; i++)
            {
                result.Frames[i].Operation.Should().Be(expected[i].Op, because: name);
                result.Frames[i].Body.Should().Equal(expected[i].Body, because: name);
            }

            if (node["expectError"]!.GetValue<bool>())
                result.Error.Should().NotBeNull(because: name);
            else
                result.Error.Should().BeNull(because: name);
        }
    }

    [Fact]
    public void Decode_Empty_Input_Yields_No_Frames()
    {
        var result = _codec.Decode([]);

        result.Frames.Should().BeEmpty();
        result.Error.Should().BeNull();
    }
}
