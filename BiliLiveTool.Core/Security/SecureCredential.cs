namespace BiliLiveTool.Core.Security;

/// <summary>
/// 机密值对象：SESSDATA / bili_jct / buvid3 等敏感值的唯一载体。
/// 禁止 ToString 落盘，内存用完 Dispose 清零。
/// </summary>
public sealed class SecureCredential : IDisposable
{
    private readonly char[] _value;

    public SecureCredential(string value) => _value = value.ToCharArray();

    /// <summary>仅供签名与 Cookie 装配等受控边界读取，禁止用于日志或序列化。</summary>
    internal string DangerousGetValue() => new(_value);

    public override string ToString() => "***";

    public void Dispose()
    {
        Array.Clear(_value, 0, _value.Length);
        GC.SuppressFinalize(this);
    }
}
