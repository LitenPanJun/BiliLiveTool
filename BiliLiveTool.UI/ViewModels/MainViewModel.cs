using System.Reflection;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.User;
using BiliLiveTool.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>
/// 主窗状态：Tab 导航（账号/直播/弹幕/控制台）、登录态头部与启动恢复，
/// 对照原 App.vue 的全局状态分发；子 VM 经事件或会话钩子回流，不反向持有本类。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int DanmuTabIndex = 2;

    private readonly UserService _user;
    private readonly AuthSessionStore _sessions;
    private readonly IAccountStore _accounts;

    public AccountViewModel Accounts { get; }

    public LiveViewModel Live { get; }

    public DanmuViewModel Danmu { get; }

    public ConsoleViewModel Console { get; }

    /// <summary>对话框宿主（窗口绑定 Dialog.Current）。</summary>
    public DialogHost Dialog { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _currentUname = "";

    [ObservableProperty]
    private string _currentRoomId = "";

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>
    /// 版本号（对照原 get_version）：按指南经 AssemblyInformationalVersion
    /// 单源于 Directory.Build.props，剥掉 SourceLink 的 +sha 后缀。
    /// </summary>
    public string VersionText { get; } = "v" + (
        typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]
        ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0");

    public MainViewModel(
        UserService user,
        AuthSessionStore sessions,
        IAccountStore accounts,
        DialogHost dialog,
        AccountViewModel accountsVm,
        LiveViewModel liveVm,
        DanmuViewModel danmuVm,
        ConsoleViewModel consoleVm)
    {
        _user = user;
        _sessions = sessions;
        _accounts = accounts;
        Dialog = dialog;
        Accounts = accountsVm;
        Live = liveVm;
        Danmu = danmuVm;
        Console = consoleVm;
        Accounts.SessionChanged += OnSessionChanged;
    }

    /// <summary>启动序列：恢复当前用户 → 同步头部 → 装载直播页（网络尽力而为）。</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        _user.InitCurrentUser();
        SyncSession();
        Accounts.Reload();
        await Live.InitializeAsync(ct).ConfigureAwait(true);
        StatusText = IsLoggedIn ? "账号已恢复" : "请扫码登录";
    }

    private void OnSessionChanged()
    {
        SyncSession();
        Danmu.OnSessionChanged();
        _ = Live.ReloadAfterSessionChangeAsync(); // 内部捕获异常，不会出未观察故障
        StatusText = IsLoggedIn ? "账号已就绪" : "未登录";
    }

    private void SyncSession()
    {
        var uid = _accounts.CurrentUid;
        var record = uid is null ? null : _accounts.Get(uid);
        IsLoggedIn = record is not null && _sessions.CurrentSnapshot.Uid != 0;
        CurrentUname = record?.Uname ?? "";
        CurrentRoomId = record?.RoomId ?? "";
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        // 对照原 DanmuPanel onActivated：切到弹幕页自动拉起监听
        if (value == DanmuTabIndex)
            _ = Danmu.ActivateAsync();
    }
}
