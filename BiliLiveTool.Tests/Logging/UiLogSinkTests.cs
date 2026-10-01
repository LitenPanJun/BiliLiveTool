using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.UI.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Tests.Logging;

/// <summary>
/// UI 日志唯一出口：写入即脱敏（JSON 走 MaskData、其余走 MaskUrl 兜底），
/// 有界 500 DropOldest，读取侧永不见原文（指南验证清单安全条）。
/// </summary>
public sealed class UiLogSinkTests
{
    private readonly SecretMasker _masker = new();

    [Fact]
    public void MaskLine_Json_Goes_Through_MaskData()
    {
        var masked = UiLogSink.MaskLine(
            _masker, """{"code":"STREAMCODE123456","message":"ok"}""");

        masked.Should().Contain("STRE*****3456");
        masked.Should().NotContain("STREAMCODE123456");
        masked.Should().Contain("\"message\":\"ok\""); // 非敏感键原样
    }

    [Fact]
    public void MaskLine_Plaintext_Url_Masks_Sensitive_Query()
    {
        var masked = UiLogSink.MaskLine(
            _masker,
            "GET https://api.bilibili.com/x/sr/qrcode/fetch?sid=1&qrcode_key=abcdef1234567890 done");

        masked.Should().NotContain("abcdef1234567890");
        masked.Should().Contain("qrcode_key=");
        masked.Should().Contain("sid=1"); // 非敏感参数保留
    }

    [Fact]
    public void MaskLine_Plain_Text_Passthrough()
    {
        const string text = "弹幕连接成功，人气 1024";
        UiLogSink.MaskLine(_masker, text).Should().Be(text);
        UiLogSink.MaskLine(_masker, "").Should().Be("");
    }

    [Fact]
    public void MaskLine_Broken_Json_Falls_Back_To_Url_Mask()
    {
        const string broken = "{\"code\":";
        // MaskData 解析失败 → MaskUrl 兜底；无 '?' 原样返回
        UiLogSink.MaskLine(_masker, broken).Should().Be(broken);
    }

    [Fact]
    public void Sink_Writes_Masked_Entries_With_Category()
    {
        using var sink = new UiLogSink(_masker);
        var logger = sink.CreateLogger("Test.Category");

        logger.LogWarning(
            "qrcode fetch https://api.bilibili.com/x/sr/qrcode/fetch?sid=1&qrcode_key=abcdef1234567890 done");

        sink.Reader.TryRead(out var entry).Should().BeTrue();
        entry!.Level.Should().Be(LogLevel.Warning);
        entry.Category.Should().Be("Test.Category");
        entry.Message.Should().NotContain("abcdef1234567890");
        entry.Line.Should().Contain("[Warning]"); // Line 拼接可用
        entry.Line.Should().Contain("Test.Category");
    }

    [Fact]
    public void Sink_Bounded_Drops_Oldest_At_500()
    {
        using var sink = new UiLogSink(_masker);
        var logger = sink.CreateLogger("Cap");

        for (var i = 0; i < 600; i++)
            logger.LogInformation("entry {Index}", i);

        var seen = new List<string>();
        while (sink.Reader.TryRead(out var entry))
            seen.Add(entry!.Message);

        seen.Should().HaveCount(500);
        seen[0].Should().Be("entry 100"); // DropOldest：前 100 条被挤出
        seen[^1].Should().Be("entry 599");
    }

    [Fact]
    public void Sink_After_Dispose_Rejects_New_Entries()
    {
        var sink = new UiLogSink(_masker);
        sink.Dispose();

        sink.CreateLogger("X").LogInformation("after-dispose");

        sink.Reader.TryRead(out _).Should().BeFalse();
    }
}
