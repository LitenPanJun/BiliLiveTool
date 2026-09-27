using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Tests.Canary;
using FluentAssertions;

namespace BiliLiveTool.Tests.Bilibili;

public class AppSignerTests
{
    [Fact]
    public void Sign_Basic_Matches_Python_Reference_Byte_For_Byte() => AssertCase("basic");

    [Fact]
    public void Sign_Special_Chars_And_Cjk_Matches_Python_Reference() => AssertCase("special");

    [Fact]
    public void Sign_Uses_Frozen_AppKey() =>
        new AppSigner().Sign(new Dictionary<string, string>())["appkey"]
            .Should().Be("aae92bc66f3edfab");

    private static void AssertCase(string name)
    {
        var testCase = CanaryVectors.Root["appSign"]![name]!;
        var input = CanaryVectors.ToStringMap(testCase["input"]!);
        var expected = testCase["expected"]!.AsObject();

        var actual = new AppSigner().Sign(input);

        actual.Should().HaveCount(expected.Count);
        foreach (var (key, value) in expected)
            actual.Should().ContainKey(key).WhoseValue.Should().Be(value!.GetValue<string>());
    }
}
