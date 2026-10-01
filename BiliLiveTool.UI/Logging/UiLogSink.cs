using System.Threading.Channels;
using BiliLiveTool.Core.Security;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.UI.Logging;

/// <summary>控制台日志条目（脱敏在写入时完成，读取侧永不见原文）。</summary>
public sealed record UiLogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public string Line => $"[{Time:HH:mm:ss}] [{Level}] {Category}: {Message}";
}

/// <summary>
/// UI 日志汇聚点：ILoggerProvider 的唯一实现，所有 ILogger 输出经
/// <see cref="ISecretMasker"/> 脱敏后进入有界通道（500，DropOldest），
/// 由 ConsoleViewModel 节流读取，对照原 FrontendLogHandler 的推送通道。
/// </summary>
public sealed class UiLogSink : ILoggerProvider
{
    private const int Capacity = 500;

    private readonly ISecretMasker _masker;
    private readonly Channel<UiLogEntry> _channel = Channel.CreateBounded<UiLogEntry>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public UiLogSink(ISecretMasker masker) => _masker = masker;

    /// <summary>供 ConsoleViewModel 节流批量读取。</summary>
    public ChannelReader<UiLogEntry> Reader => _channel.Reader;

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void Dispose() => _channel.Writer.TryComplete();

    /// <summary>整行脱敏：JSON 载荷走 MaskData，其余走 MaskUrl 兜底。</summary>
    internal static string MaskLine(ISecretMasker masker, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var trimmed = message.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var masked = masker.MaskData(message);
            if (!string.Equals(masked, "Parse Error", StringComparison.Ordinal))
                return masked;
        }

        return masker.MaskUrl(message);
    }

    private void Write(LogLevel level, string category, string message)
    {
        var masked = MaskLine(_masker, message);
        _channel.Writer.TryWrite(new UiLogEntry(DateTimeOffset.Now, level, category, masked));
    }

    private sealed class SinkLogger(UiLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} [{exception.GetType().Name}: {exception.Message}]";
            sink.Write(logLevel, category, message);
        }
    }
}
