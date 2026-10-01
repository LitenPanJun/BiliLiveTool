using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Services;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Live;
using BiliLiveTool.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>推流端点行：地址与码分列，均仅可经复制出栈。</summary>
public sealed record EndpointRow(
    string Label,
    string Addr,
    string Code,
    IAsyncRelayCommand<string?> CopyCommand);

/// <summary>
/// 直播页：分区选择、标题公告、开播停播与推流码展示，
/// 对照原 StreamPanel/RtmpPanel：doToggle 先更新标题公告再开播，
/// 人脸分支 60024/60043 弹统一对话框给 URL（仅可复制）。
/// </summary>
public sealed partial class LiveViewModel : ObservableObject
{
    private readonly LiveService _live;
    private readonly AuthSessionStore _sessions;
    private readonly IAccountStore _accounts;
    private readonly IDialogService _dialog;
    private readonly ClipboardHub _clipboard;
    private readonly ILogger<LiveViewModel> _log;

    private string _savedTitle = "";
    private string _savedAnnouncement = "";

    public ObservableCollection<string> ParentNames { get; } = [];

    public ObservableCollection<string> ChildNames { get; } = [];

    [ObservableProperty] private string _selectedParent = "";
    [ObservableProperty] private string _selectedChild = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _announcement = "";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    /// <summary>推流端点行（复制命令随行携带，避免视图回溯主 VM）。</summary>
    public ObservableCollection<EndpointRow> Endpoints { get; } = [];

    /// <summary>是否已有可展示的推流端点。</summary>
    public bool HasEndpoints => Endpoints.Count > 0;

    /// <summary>空闲态（开播/停播按钮可用性绑定）。</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>未开播徽标绑定。</summary>
    public bool IsOffline => !IsLive;

    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(IsOffline));

    public LiveViewModel(
        LiveService live,
        AuthSessionStore sessions,
        IAccountStore accounts,
        IDialogService dialog,
        ClipboardHub clipboard,
        ILogger<LiveViewModel> log)
    {
        _live = live;
        _sessions = sessions;
        _accounts = accounts;
        _dialog = dialog;
        _clipboard = clipboard;
        _log = log;
    }

    /// <summary>启动预填：账号记录兜底 + 分区拉取 + 房间资料同步（失败静默，不弹窗）。</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var uid = _accounts.CurrentUid;
        var record = uid is null ? null : _accounts.Get(uid);
        if (record is not null)
        {
            if (record.LastTitle.Length > 0)
                Title = _savedTitle = record.LastTitle;
            if (record.LastAnnouncement.Length > 0)
                Announcement = _savedAnnouncement = record.LastAnnouncement;
        }

        IsLive = _sessions.CurrentSnapshot.IsLive;

        try
        {
            await _live.GetPartitionsAsync(ct).ConfigureAwait(true);
            LoadPartitions();

            var res = await _live.SyncRoomProfileAsync(ct).ConfigureAwait(true);
            if (res.IsSuccess)
                ApplyProfile(res.Data);
        }
        catch (Exception e)
        {
            // 启动路径不弹窗：异常经日志脱敏后入控制台
            _log.LogWarning(e, "Init live panel failed");
        }
    }

    /// <summary>会话变化（登录/切换）后由主窗调用：重载分区资料与直播态。</summary>
    public async Task ReloadAfterSessionChangeAsync()
    {
        IsLive = _sessions.CurrentSnapshot.IsLive;
        ClearEndpoints();
        await InitializeAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SyncProfileAsync()
    {
        var res = await _live.SyncRoomProfileAsync(CancellationToken.None).ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("同步失败", FailText(res.Message)).ConfigureAwait(true);
            return;
        }

        ApplyProfile(res.Data);
        StatusText = "直播信息已同步";
    }

    [RelayCommand]
    private async Task UpdateTitleAsync()
    {
        var res = await _live.UpdateTitleAsync(Title, CancellationToken.None).ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("更新标题失败", FailText(res.Message)).ConfigureAwait(true);
            return;
        }

        _savedTitle = Title;
        StatusText = "标题已更新";
    }

    [RelayCommand]
    private async Task UpdateAnnouncementAsync()
    {
        var res = await _live
            .UpdateAnnouncementAsync(Announcement, CancellationToken.None)
            .ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("更新公告失败", FailText(res.Message)).ConfigureAwait(true);
            return;
        }

        _savedAnnouncement = Announcement;
        StatusText = "公告已更新";
    }

    [RelayCommand]
    private async Task UpdateAreaAsync()
    {
        if (SelectedParent.Length == 0 || SelectedChild.Length == 0)
        {
            StatusText = "请先选择分区";
            return;
        }

        var res = await _live
            .UpdateAreaAsync(SelectedParent, SelectedChild, CancellationToken.None)
            .ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("更新分区失败", FailText(res.Message)).ConfigureAwait(true);
            return;
        }

        StatusText = $"分区已更新：{SelectedParent} - {SelectedChild}";
    }

    [RelayCommand]
    private async Task StartLiveAsync()
    {
        if (IsBusy)
            return;

        if (_sessions.CurrentSnapshot.Uid == 0)
        {
            await _dialog.ShowMessageAsync("无法开播", "请先登录").ConfigureAwait(true);
            return;
        }

        IsBusy = true;
        try
        {
            // 对照原 StreamPanel doToggle：先标题、公告，再开播
            if (Title.Trim().Length > 0 && Title != _savedTitle)
            {
                var titleRes = await _live.UpdateTitleAsync(Title, CancellationToken.None).ConfigureAwait(true);
                if (!titleRes.IsSuccess)
                {
                    await _dialog.ShowMessageAsync("更新标题失败", FailText(titleRes.Message)).ConfigureAwait(true);
                    return;
                }

                _savedTitle = Title;
            }

            if (Announcement != _savedAnnouncement)
            {
                var noticeRes = await _live
                    .UpdateAnnouncementAsync(Announcement, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!noticeRes.IsSuccess)
                {
                    await _dialog.ShowMessageAsync("更新公告失败", FailText(noticeRes.Message)).ConfigureAwait(true);
                    return;
                }

                _savedAnnouncement = Announcement;
            }

            var result = await _live
                .StartLiveAsync(SelectedParent, SelectedChild, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsOk && result.Value is { } endpoints)
            {
                IsLive = true;
                ApplyEndpoints(endpoints);
                StatusText = "直播已开启";
                return;
            }

            var verify = result.Error;
            await _dialog.ShowMessageAsync(
                "需要人脸验证",
                "开播需完成人脸验证，请复制以下链接在浏览器完成验证后重试。",
                verify?.Qr).ConfigureAwait(true);
        }
        catch (BilibiliException e)
        {
            await _dialog.ShowMessageAsync("开播失败", FailText(e.Message)).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopLiveAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var res = await _live.StopLiveAsync(CancellationToken.None).ConfigureAwait(true);
            if (!res.IsSuccess)
            {
                await _dialog.ShowMessageAsync("停播失败", FailText(res.Message)).ConfigureAwait(true);
                return;
            }

            IsLive = false;
            ClearEndpoints();
            StatusText = "直播已结束";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var ok = await _clipboard.TrySetTextAsync(text).ConfigureAwait(true);
        StatusText = ok ? "已复制到剪贴板" : "复制失败，请重试";
    }

    // --- 数据装载 ---

    partial void OnSelectedParentChanged(string value) => RebuildChildren(value);

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    private void RebuildChildren(string parent)
    {
        ChildNames.Clear();
        if (parent.Length > 0 && _sessions.Partitions.TryGetValue(parent, out var subs))
        {
            foreach (var child in subs.Keys)
                ChildNames.Add(child);
        }

        SelectedChild = ChildNames.Count > 0 ? ChildNames[0] : "";
    }

    private void LoadPartitions()
    {
        var snapshot = _sessions.CurrentSnapshot;
        var wanted = snapshot.CurrentAreaNames;
        if (wanted.Count < 2)
        {
            var uid = _accounts.CurrentUid;
            var record = uid is null ? null : _accounts.Get(uid);
            wanted = record?.LastAreaName ?? [];
        }

        var parent = wanted.Count > 0 ? wanted[0] : "";
        var child = wanted.Count > 1 ? wanted[1] : "";

        ParentNames.Clear();
        foreach (var key in _sessions.Partitions.Keys)
            ParentNames.Add(key);

        SelectedParent = ParentNames.Contains(parent) ? parent : ParentNames.FirstOrDefault() ?? "";
        RebuildChildren(SelectedParent);
        if (ChildNames.Contains(child))
            SelectedChild = child;
    }

    private void ApplyProfile(JsonObject? data)
    {
        if (data is null)
            return;

        var title = ReadText(data["last_title"]);
        if (title.Length > 0)
            Title = _savedTitle = title;

        var announcement = ReadText(data["last_announcement"]);
        if (announcement.Length > 0)
            Announcement = _savedAnnouncement = announcement;

        if (data["last_area_name"] is JsonArray names && names.Count >= 2)
        {
            var parent = ReadText(names[0]);
            var child = ReadText(names[1]);
            if (parent.Length > 0)
            {
                if (ParentNames.Contains(parent))
                    SelectedParent = parent;
                if (ChildNames.Contains(child))
                    SelectedChild = child;
            }
        }
    }

    private void ApplyEndpoints(LiveStreamEndpoints endpoints)
    {
        Endpoints.Clear();
        if (endpoints.Rtmp1.Addr.Length > 0 || endpoints.Rtmp1.Code.Length > 0)
            Endpoints.Add(new EndpointRow("RTMP", endpoints.Rtmp1.Addr, endpoints.Rtmp1.Code, CopyCommand));
        if (endpoints.Rtmp2.Addr.Length > 0 || endpoints.Rtmp2.Code.Length > 0)
            Endpoints.Add(new EndpointRow("RTMP 备线", endpoints.Rtmp2.Addr, endpoints.Rtmp2.Code, CopyCommand));
        if (endpoints.Srt.Addr.Length > 0 || endpoints.Srt.Code.Length > 0)
            Endpoints.Add(new EndpointRow("SRT", endpoints.Srt.Addr, endpoints.Srt.Code, CopyCommand));
        OnPropertyChanged(nameof(HasEndpoints));
    }

    private void ClearEndpoints()
    {
        Endpoints.Clear();
        OnPropertyChanged(nameof(HasEndpoints));
    }

    private static string ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string FailText(string message) =>
        message.Length == 0 ? "操作失败" : message;
}
