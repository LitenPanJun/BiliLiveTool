using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.UI.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Tests.Logging;

/// <summary>
/// 落盘日志：与控制台同源同脱敏、毫秒时间戳、超 2MB 轮转 app.log.old、
/// 文件不可用时降级为仅 UI 通道（不阻断日志链路）。
/// </summary>
public sealed class UiLogSinkFileTests : IDisposable
{
    private const string Secret = "abcdef1234567890";

    private readonly SecretMasker _masker = new();
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "bili-log-test-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "logs", "app.log");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理尽力而为
        }
    }

    [Fact]
    public void File_Writes_Masked_Line_With_Millisecond_Timestamp()
    {
        var sink = new UiLogSink(_masker, LogPath);
        var logger = sink.CreateLogger("AuthService");

        // 真实日志形态：敏感值出现在带 ? 的 URL query（复刻 _mask_url 语义）
        logger.LogWarning(
            $"qrcode fetch https://api.bilibili.com/x/sr/qrcode/fetch?sid=1&qrcode_key={Secret} done");

        sink.Dispose();
        var text = File.ReadAllText(LogPath);
        text.Should().Contain("[Warning]");
        text.Should().Contain("AuthService");
        text.Should().MatchRegex(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]");
        text.Should().NotContain(Secret);
    }

    [Fact]
    public void File_Rotates_When_Reaching_Cap()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('x', 2 * 1024 * 1024));

        var sink = new UiLogSink(_masker, LogPath);
        sink.CreateLogger("X").LogInformation("after-rotate");
        sink.Dispose();

        File.Exists(LogPath + ".old").Should().BeTrue("超限旧日志应轮转为 .old");
        new FileInfo(LogPath + ".old").Length.Should().Be(2 * 1024 * 1024);
        File.ReadAllText(LogPath).Should().Contain("after-rotate");
    }

    [Fact]
    public void File_Unavailable_Degrades_To_Channel_Only()
    {
        // 用普通文件占住目录位置：Directory.CreateDirectory 抛 IOException → 仅 UI
        var blocker = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "occupied");

        var sink = new UiLogSink(_masker, Path.Combine(blocker, "app.log"));
        var logger = sink.CreateLogger("Cat");
        logger.LogInformation("still-in-channel");

        sink.Reader.TryRead(out var entry).Should().BeTrue("文件不可用时通道仍须工作");
        entry!.Message.Should().Be("still-in-channel");
        sink.Dispose();
    }
}
