using System.Collections.ObjectModel;
using Avalonia.Threading;
using BiliLiveTool.UI.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>
/// 控制台页：经 UiLogSink 的 ChannelReader 节流批量读取（200ms 一拍、
/// 单拍上限 200 行），行数封顶 500，对照原 ConsolePanel logs[500]。
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
    private const int MaxLines = 500;
    private const int BatchSize = 200;

    private readonly UiLogSink _sink;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<string> Lines { get; } = [];

    public ConsoleViewModel(UiLogSink sink)
    {
        _sink = sink;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    /// <summary>停表（退出或宿主卸载时调用，避免退出期再写集合）。</summary>
    public void Stop() => _timer.Stop();

    [RelayCommand]
    private void Clear() => Lines.Clear();

    private void Drain()
    {
        var added = 0;
        while (added < BatchSize && _sink.Reader.TryRead(out var entry))
        {
            Lines.Add(entry.Line);
            added++;
        }

        if (added == 0)
            return;

        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
    }
}
