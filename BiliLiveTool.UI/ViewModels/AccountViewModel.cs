using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.User;
using BiliLiveTool.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace BiliLiveTool.UI.ViewModels;

/// <summary>
/// 账号列表行：命令随行携带（避免视图 $parent 回溯主 VM），
/// 头像不加载网络图，机密与 uid 一律掩码展示。
/// </summary>
public sealed class AccountItem
{
    public AccountItem(
        string uid,
        string maskedUid,
        string uname,
        string roomId,
        bool isCurrent,
        IAsyncRelayCommand<string?> switchCommand,
        IAsyncRelayCommand<string?> logoutCommand)
    {
        Uid = uid;
        MaskedUid = maskedUid;
        Uname = uname;
        RoomId = roomId;
        IsCurrent = isCurrent;
        SwitchCommand = switchCommand;
        LogoutCommand = logoutCommand;
    }

    public string Uid { get; }

    public string MaskedUid { get; }

    public string Uname { get; }

    public string RoomId { get; }

    public bool IsCurrent { get; }

    public IAsyncRelayCommand<string?> SwitchCommand { get; }

    public IAsyncRelayCommand<string?> LogoutCommand { get; }
}

/// <summary>
/// 账号页：扫码登录（1500ms 轮询节奏在此驱动，对照原 QrCodeLogin 的
/// setInterval）、账号列表与切换登出。86038 过期自动重取二维码、
/// 86090 已扫提示确认；登录链成功后广播 SessionChanged 供主窗刷新。
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly UserService _user;
    private readonly IAccountStore _accounts;
    private readonly ISecretMasker _masker;
    private readonly IDialogService _dialog;

    private CancellationTokenSource? _pollCts;

    public event Action? SessionChanged;

    public ObservableCollection<AccountItem> AccountItems { get; } = [];

    [ObservableProperty]
    private bool _isAddingAccount;

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    private Bitmap? _qrImage;

    [ObservableProperty]
    private string _qrStatus = "请生成二维码登录";

    [ObservableProperty]
    private string _statusText = "";

    public AccountViewModel(
        AuthService auth,
        UserService user,
        IAccountStore accounts,
        ISecretMasker masker,
        IDialogService dialog)
    {
        _auth = auth;
        _user = user;
        _accounts = accounts;
        _masker = masker;
        _dialog = dialog;
    }

    /// <summary>按账号注册表重建列表（本地同步，无网络）。</summary>
    public void Reload()
    {
        var currentUid = _accounts.CurrentUid;
        AccountItems.Clear();
        foreach (var account in _accounts.List())
        {
            AccountItems.Add(new AccountItem(
                account.Uid,
                _masker.MaskString(account.Uid, 2, 2),
                account.Uname,
                account.RoomId,
                account.Uid == currentUid,
                SwitchAccountCommand,
                LogoutCommand));
        }

        // 未登录时二维码面板常驻；已登录默认收起
        IsAddingAccount = currentUid is null;
    }

    [RelayCommand]
    private void AddAccount() => IsAddingAccount = true;

    [RelayCommand]
    private async Task BeginLoginAsync()
    {
        if (IsLoggingIn)
            return;

        CancelPoll();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        IsLoggingIn = true;
        IsAddingAccount = true;

        try
        {
            var key = await CreateQrAsync().ConfigureAwait(true);
            if (key is null)
            {
                QrStatus = "获取二维码失败，请重试";
                return;
            }

            await PollLoopAsync(key, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            QrStatus = "已取消登录";
        }
        finally
        {
            IsLoggingIn = false;
            CancelPoll();
        }
    }

    [RelayCommand]
    private void CancelLogin()
    {
        CancelPoll();
        // 未登录时面板必须保留，否则无路可走
        IsAddingAccount = _accounts.CurrentUid is null;
    }

    [RelayCommand]
    private async Task SwitchAccountAsync(string? uid)
    {
        if (string.IsNullOrEmpty(uid) || uid == _accounts.CurrentUid)
            return;

        CancelPoll();
        var res = await _user.SwitchAccountAsync(uid, CancellationToken.None).ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("切换账号失败", res.Message).ConfigureAwait(true);
            return;
        }

        Reload();
        StatusText = "已切换账号";
        SessionChanged?.Invoke();
    }

    [RelayCommand]
    private async Task LogoutAsync(string? uid)
    {
        if (string.IsNullOrEmpty(uid))
            return;

        var uname = _accounts.Get(uid)?.Uname ?? uid;
        var confirmed = await _dialog
            .ConfirmAsync("退出登录", $"确定退出 {uname}？本机保存的登录态将被删除。")
            .ConfigureAwait(true);
        if (!confirmed)
            return;

        CancelPoll();
        var res = await _user.LogoutAsync(uid, CancellationToken.None).ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("退出登录失败", res.Message).ConfigureAwait(true);
            return;
        }

        Reload();
        StatusText = "已退出登录";
        SessionChanged?.Invoke();
    }

    [RelayCommand]
    private async Task RefreshUserAsync()
    {
        var res = await _user.RefreshCurrentUserAsync(CancellationToken.None).ConfigureAwait(true);
        if (!res.IsSuccess)
        {
            await _dialog.ShowMessageAsync("刷新失败", res.Message).ConfigureAwait(true);
            return;
        }

        Reload();
        StatusText = "账号资料已刷新";
        SessionChanged?.Invoke();
    }

    [RelayCommand]
    private void RefreshAccounts() => Reload();

    // --- 扫码登录 ---

    private async Task<string?> CreateQrAsync()
    {
        var res = await _auth.GetLoginQrcodeAsync(CancellationToken.None).ConfigureAwait(true);
        if (!res.IsSuccess || res.Data is not { } data)
            return null;

        var url = ReadText(data["url"]);
        var key = ReadText(data["qrcode_key"]);
        if (url.Length == 0 || key.Length == 0)
            return null;

        QrImage = RenderQr(url);
        QrStatus = "请使用哔哩哔哩 App 扫码登录";
        return key;
    }

    private async Task PollLoopAsync(string key, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // 对照原 setInterval(1500)：首轮也在 1500ms 之后
            await Task.Delay(1500, token).ConfigureAwait(true);
            var res = await _auth.PollLoginStatusAsync(key, token).ConfigureAwait(true);

            if (res.IsSuccess)
            {
                QrStatus = "登录成功";
                IsAddingAccount = false;
                Reload();
                SessionChanged?.Invoke();
                return;
            }

            if (res.Code == 86090)
            {
                QrStatus = "已扫码，请在手机上确认";
            }
            else if (res.Code == 86038)
            {
                QrStatus = "二维码已过期，正在刷新";
                var fresh = await CreateQrAsync().ConfigureAwait(true);
                if (fresh is null)
                {
                    QrStatus = "刷新二维码失败，请重试";
                    return;
                }

                key = fresh;
            }
            else
            {
                QrStatus = res.Message.Length == 0 ? "网络异常，正在重试" : res.Message;
            }
        }
    }

    private void CancelPoll()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private static string ReadText(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : "";

    private static Bitmap? RenderQr(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var code = new PngByteQRCode(data);
            var png = code.GetGraphic(8, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 });
            return new Bitmap(new MemoryStream(png));
        }
        catch (Exception)
        {
            return null; // 二维码渲染失败降级为手动输入 URL（状态栏提示）
        }
    }
}
