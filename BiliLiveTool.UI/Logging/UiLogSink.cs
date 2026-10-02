using System.Threading.Channels;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Services.Config;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.UI.Logging;

/// <summary>控制台日志条目（脱敏在写入时完成，读取侧永不见原文）。</summary>
public sealed record UiLogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public string Line => $"[{Time:HH:mm:ss.fff}] [{Level}] {Category}: {Message}";
}

/// <summary>
/// UI 日志汇聚点：ILoggerProvider 的唯一实现，所有 ILogger 输出经
/// <see cref="ISecretMasker"/> 脱敏后进入有界通道（500，DropOldest），
/// 由 ConsoleViewModel 节流读取；同时以毫秒时间戳追加落盘
/// <c>logs/app.log</c>（与控制台同源同脱敏，超 2MB 轮转为 app.log.old，
/// 目录 700 / 文件 600），供进程退出后取证与复制；文件不可用时降级为
/// 仅 UI。对照原 FrontendLogHandler 的推送通道。
/// </summary>
public sealed class UiLogSink : ILoggerProvider
{
    private const int Capacity = 500;
    private const long RotateBytes = 2 * 1024 * 1024;

    private readonly ISecretMasker _masker;
    private readonly Channel<UiLogEntry> _channel = Channel.CreateBounded<UiLogEntry>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly object _fileGate = new();
    private StreamWriter? _file;

    public UiLogSink(ISecretMasker masker)
        : this(masker, DefaultLogPath())
    {
    }

    /// <param name="filePath">日志文件路径；null 或不可用时仅走 UI 通道（测试注入用）。</param>
    internal UiLogSink(ISecretMasker masker, string? filePath)
    {
        _masker = masker;
        _file = OpenFile(filePath);
    }

    /// <summary>供 ConsoleViewModel 节流批量读取。</summary>
    public ChannelReader<UiLogEntry> Reader => _channel.Reader;

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        lock (_fileGate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>整行脱敏：JSON 走 MaskData，其余走 MaskUrl 兜底。</summary>
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

    private static string DefaultLogPath() =>
        Path.Combine(FileConfigStore.ConfigDirectory(), "logs", "app.log");

    /// <summary>打开轮转日志文件；任何不可用都返回 null（降级为仅 UI）。</summary>
    private static StreamWriter? OpenFile(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                Tighten(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (File.Exists(path) && new FileInfo(path).Length >= RotateBytes)
                File.Move(path, path + ".old", overwrite: true);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            Tighten(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void Tighten(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            // 权限收紧失败不阻断日志写入
        }
    }

    private void Write(LogLevel level, string category, string message)
    {
        var masked = MaskLine(_masker, message);
        var entry = new UiLogEntry(DateTimeOffset.Now, level, category, masked);
        _channel.Writer.TryWrite(entry);
        AppendLine(entry.Line);
    }

    private void AppendLine(string line)
    {
        lock (_fileGate)
        {
            if (_file is null)
                return;
            try
            {
                _file.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 落盘失败不阻断 UI 日志：丢弃句柄，降级为仅 UI
                _file.Dispose();
                _file = null;
            }
        }
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
