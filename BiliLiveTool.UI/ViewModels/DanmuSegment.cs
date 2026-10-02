using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BiliLiveTool.Core.Danmu;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>
/// 弹幕行内片段：纯文本或表情图。表情图懒加载（首次绑定 Source 触发下载），
/// 加载失败保持 token 文本降级；CDN 的 http 地址统一升级为 https（HttpClient 仅走 HTTPS）。
/// </summary>
public sealed class DanmuSegment : INotifyPropertyChanged
{
    private const int MaxEdge = 72; // 表情贴片最大边（装扮表情 162px 收敛，避免撑爆行高）
    private const int CacheLimit = 1024;

    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Downloads = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string? _imageUrl;
    private Bitmap? _bitmap;
    private int _loadStarted;

    private DanmuSegment(string text, int width, int height, string? imageUrl)
    {
        Text = text;
        Width = width;
        Height = height;
        _imageUrl = imageUrl;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>片段文本：文本段为原文，表情段为 [token] 或整条内容。</summary>
    public string Text { get; }

    /// <summary>表情显示宽度（文本段为 0）。</summary>
    public int Width { get; }

    /// <summary>表情显示高度（文本段为 0）。</summary>
    public int Height { get; }

    /// <summary>是否为表情图段。</summary>
    public bool IsImage => _imageUrl is not null;

    /// <summary>表情图片地址（已归一为 https；文本段为 null）。</summary>
    public string? ImageUrl => _imageUrl;

    /// <summary>显示文本（图片未就绪或加载失败时降级显示 token）。</summary>
    public bool ShowText => _imageUrl is null || _bitmap is null;

    /// <summary>显示图片（仅图片就绪后）。</summary>
    public bool ShowImage => _bitmap is not null;

    /// <summary>表情位图；首次读取触发下载，绑定 Source 即懒加载。</summary>
    public Bitmap? Bitmap
    {
        get
        {
            if (_imageUrl is not null && Interlocked.Exchange(ref _loadStarted, 1) == 0)
                _ = LoadAsync();
            return _bitmap;
        }
    }

    public static DanmuSegment FromText(string text) => new(text, 0, 0, null);

    public static DanmuSegment FromEmote(string token, DanmuEmote emote)
    {
        var (w, h) = Fit(emote.Width, emote.Height);
        return new(token, w, h, ToHttps(emote.Url));
    }

    /// <summary>
    /// 按 [token] 切分并查表情表（对照网页端按 descript 匹配 emots）：
    /// 无表情表或无命中返回 null（整行走纯文本）；整条内容命中（单表情弹幕）产出单图段。
    /// </summary>
    public static IReadOnlyList<DanmuSegment>? Build(
        string msg,
        IReadOnlyDictionary<string, DanmuEmote>? emotes)
    {
        if (emotes is null || emotes.Count == 0 || msg.Length == 0)
            return null;
        if (emotes.TryGetValue(msg, out var whole))
            return [FromEmote(msg, whole)];

        List<DanmuSegment>? parts = null;
        var textStart = 0;
        var i = 0;
        while (i < msg.Length)
        {
            if (msg[i] == '[')
            {
                var close = msg.IndexOf(']', i + 1);
                if (close > i && emotes.TryGetValue(msg.Substring(i, close - i + 1), out var emote))
                {
                    if (i > textStart)
                    {
                        parts ??= [];
                        parts.Add(FromText(msg.Substring(textStart, i - textStart)));
                    }

                    parts ??= [];
                    parts.Add(FromEmote(msg.Substring(i, close - i + 1), emote));
                    i = close + 1;
                    textStart = i;
                    continue;
                }
            }

            i++;
        }

        if (parts is null)
            return null; // 无命中 token：整行保底纯文本
        if (textStart < msg.Length)
            parts.Add(FromText(msg.Substring(textStart)));
        return parts;
    }

    /// <summary>等比收敛到最大边；缺省尺寸按 20×20。</summary>
    private static (int W, int H) Fit(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (20, 20);
        var max = Math.Max(width, height);
        return max <= MaxEdge
            ? (width, height)
            : (Math.Max(1, width * MaxEdge / max), Math.Max(1, height * MaxEdge / max));
    }

    private static string ToHttps(string url) =>
        url.StartsWith("http://", StringComparison.Ordinal)
            ? "https://" + url["http://".Length..]
            : url;

    private async Task LoadAsync()
    {
        try
        {
            var bitmap = await DownloadAsync(_imageUrl!);
            if (bitmap is null)
                return;
            await Dispatcher.UIThread.InvokeAsync(() => SetBitmap(bitmap));
        }
        catch (Exception)
        {
            // 失败：保持文本降级，并移除缓存允许后续行重试
            Downloads.TryRemove(_imageUrl!, out _);
        }
    }

    private static Task<Bitmap?> DownloadAsync(string url)
    {
        if (Downloads.Count >= CacheLimit)
            Downloads.Clear(); // 只清下载缓存（位图随行 GC），防长跑无界增长
        return Downloads.GetOrAdd(url, DownloadCoreAsync);
    }

    private static async Task<Bitmap?> DownloadCoreAsync(string url)
    {
        var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        await using var ms = new MemoryStream(bytes);
        return await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(ms));
    }

    private void SetBitmap(Bitmap bitmap)
    {
        _bitmap = bitmap;
        Raise(nameof(Bitmap));
        Raise(nameof(ShowImage));
        Raise(nameof(ShowText));
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
