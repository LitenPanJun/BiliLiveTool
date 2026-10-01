using System.Collections.ObjectModel;
using Avalonia.Threading;
using BiliLiveTool.Core.Danmu;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Danmu;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>弹幕列表行（时间 + 渲染完成的文本）。</summary>
public sealed record DanmuItem(string Time, string Display);

/// <summary>
/// 弹幕页：经 DanmuService.Events 的 ChannelReader 以 100ms 节拍批量渲染，
/// 上限 200 条虚拟化展示（指南步骤 1），弹幕风暴由通道背压吸收不压 UI 线程；
/// 发送草稿与错误码提示对照原 DanmuPanel。
/// </summary>
public sealed partial class DanmuViewModel : ObservableObject
{
    private const int MaxMessages = 200;
    private const int BatchSize = 100;

    private readonly DanmuService _danmu;
    private readonly AuthSessionStore _sessions;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<DanmuItem> Messages { get; } = [];

    [ObservableProperty]
    private string _draft = "";

    [ObservableProperty]
    private bool _isMonitoring;

    [ObservableProperty]
    private string _statusText = "弹幕未连接";

    /// <summary>未监听徽标绑定。</summary>
    public bool IsStopped => !IsMonitoring;

    partial void OnIsMonitoringChanged(bool value) => OnPropertyChanged(nameof(IsStopped));

    public DanmuViewModel(DanmuService danmu, AuthSessionStore sessions)
    {
        _danmu = danmu;
        _sessions = sessions;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    /// <summary>停表（退出时调用，退出期不再改集合）。</summary>
    public void StopPump() => _timer.Stop();

    /// <summary>进入弹幕页自动激活（对照原 DanmuPanel onActivated）。</summary>
    public async Task ActivateAsync()
    {
        if (IsMonitoring)
            return;

        var roomId = _sessions.CurrentSnapshot.RoomId;
        if (roomId.Length == 0)
        {
            StatusText = "未获取到房间ID，请先登录";
            return;
        }

        try
        {
            var ok = await _danmu.ConnectAsync(roomId, CancellationToken.None).ConfigureAwait(true);
            IsMonitoring = ok;
            StatusText = ok ? "弹幕已连接" : "弹幕连接失败，可重试";
        }
        catch (Exception)
        {
            IsMonitoring = false;
            StatusText = "弹幕连接异常，可重试";
        }
    }

    /// <summary>会话切换/登出后由主窗调用：服务端已停旧连接，同步 UI 态。</summary>
    public void OnSessionChanged()
    {
        IsMonitoring = false;
        StatusText = "弹幕未连接";
    }

    [RelayCommand]
    private async Task StartMonitorAsync() => await ActivateAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task StopMonitorAsync()
    {
        try
        {
            await _danmu.StopAsync(CancellationToken.None).ConfigureAwait(true);
            IsMonitoring = false;
            StatusText = "弹幕已停止";
        }
        catch (Exception e)
        {
            StatusText = $"停止失败：{e.Message}";
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0)
            return;

        try
        {
            var res = await _danmu.SendDanmuAsync(text, CancellationToken.None).ConfigureAwait(true);
            if (!res.IsSuccess)
            {
                StatusText = res.Message.Length == 0 ? "发送失败" : res.Message;
                // 对照指南示例：失败也清空草稿
                Draft = "";
                return;
            }

            Draft = "";
            StatusText = "";
        }
        catch (Exception e)
        {
            Draft = "";
            StatusText = $"发送异常：{e.Message}";
        }
    }

    private void Drain()
    {
        var added = 0;
        while (added < BatchSize && _danmu.Events.TryRead(out var evt))
        {
            Messages.Add(new DanmuItem(DateTime.Now.ToString("HH:mm:ss"), Format(evt)));
            added++;
        }

        if (added == 0)
            return;

        while (Messages.Count > MaxMessages)
            Messages.RemoveAt(0);
    }

    private static string Format(DanmuEvent evt) => evt.Type switch
    {
        DanmuEventTypes.Danmu => $"{evt.Uname}: {evt.Msg}",
        DanmuEventTypes.Interact => evt.Uname.Length == 0 ? evt.Msg : $"{evt.Uname} {evt.Msg}",
        DanmuEventTypes.Gift => evt.Msg.Length > 0
            ? $"{evt.Uname} {evt.Msg}"
            : $"{evt.Uname} {evt.Action} {evt.GiftName} x{evt.Num}",
        _ => evt.Msg, // system：连接成功/熔断等直出
    };
}
