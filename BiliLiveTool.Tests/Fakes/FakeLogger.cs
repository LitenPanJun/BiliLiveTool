using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Tests.Fakes;

/// <summary>捕获日志条目，供断言 debug/info/error 级别与文案。</summary>
internal sealed class FakeLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public bool Has(LogLevel level, string substring) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(substring, StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
