namespace BiliLiveTool.Core.Security;

/// <summary>
/// 账号机密存储缝：值为 <see cref="SecureCredential"/>，禁止 ToString 或序列化。
/// P1 为进程内存实现，系统钥匙串后端随 P2 落地。
/// </summary>
public interface ISecretVault
{
    /// <summary>保存某账号的整套机密（整体替换）。</summary>
    void Save(string uid, IReadOnlyDictionary<string, SecureCredential> secrets);

    /// <summary>读取某账号的机密集合；不存在时返回 null。</summary>
    IReadOnlyDictionary<string, SecureCredential>? Load(string uid);

    /// <summary>删除并清零该账号的机密。</summary>
    bool Remove(string uid);
}
