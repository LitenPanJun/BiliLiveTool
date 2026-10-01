using Avalonia.Input.Platform;

namespace BiliLiveTool.UI.Services;

/// <summary>
/// 剪贴板服务缝：推流码等仅经复制按钮写入剪贴板（指南验收
/// “推流码仅可复制”），窗口创建后由 App 注入 TopLevel 剪贴板。
/// </summary>
public sealed class ClipboardHub
{
    public IClipboard? Backend { get; set; }

    public async Task<bool> TrySetTextAsync(string text)
    {
        if (Backend is not { } backend || string.IsNullOrEmpty(text))
            return false;

        try
        {
            await backend.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            return false; // 剪贴板被占用等瞬时失败，交由调用方提示
        }
    }
}
