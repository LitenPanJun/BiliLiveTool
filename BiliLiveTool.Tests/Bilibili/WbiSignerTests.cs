using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Tests.Canary;
using FluentAssertions;

namespace BiliLiveTool.Tests.Bilibili;

public class WbiSignerTests
{
    [Fact]
    public void GetMixinKey_Shuffles_And_Truncates_To_32_Chars()
    {
        var testCase = CanaryVectors.Root["mixinKey"]!;
        WbiSigner.GetMixinKey(testCase["orig"]!.GetValue<string>())
            .Should().Be(testCase["expected"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("webLocation")]
    [InlineData("filter")]
    [InlineData("danmuInfo")]
    public void Sign_Matches_Python_Reference_Byte_For_Byte(string name)
    {
        var testCase = CanaryVectors.Root["wbi"]![name]!;
        var input = CanaryVectors.ToStringMap(testCase["input"]!);
        var expected = testCase["expected"]!.AsObject();
        var wts = testCase["wts"]!.GetValue<long>();

        var signer = new WbiSigner();
        var actual = signer.Sign(
            input,
            testCase["imgKey"]!.GetValue<string>(),
            testCase["subKey"]!.GetValue<string>(),
            wts);

        actual.Should().HaveCount(expected.Count);
        foreach (var (key, value) in expected)
            actual.Should().ContainKey(key).WhoseValue.Should().Be(value!.GetValue<string>());
    }

    [Fact]
    public void Sign_Without_Explicit_Wts_Uses_Current_Time()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var actual = new WbiSigner().Sign(
            new Dictionary<string, string> { ["web_location"] = "444.8" },
            "7cd084941338484aae1ad9425b84077c",
            "4932caff0ff746eab6f01bf08b70ac45");
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var wts = long.Parse(actual["wts"]);
        wts.Should().BeInRange(before, after);
        actual["w_rid"].Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
