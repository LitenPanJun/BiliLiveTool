namespace BiliLiveTool.Core.Auth;

/// <summary>
/// 账号注册表缝：方法语义对照原 user_service 的 save/switch/logout/load；
/// P1 为进程内实现，持久化后端随 P2 落地。
/// </summary>
public interface IAccountStore
{
    string? CurrentUid { get; }

    AccountRecord? Get(string uid);

    IReadOnlyList<AccountRecord> List();

    /// <summary>写入账号并置为当前（对照 save_user_data）。</summary>
    void Save(AccountRecord account);

    /// <summary>仅更新账号，不改变当前指针（对照 _save_current_user_fields）。</summary>
    void Upsert(AccountRecord account);

    /// <summary>切换当前账号（对照 switch_account 的 current_uid 赋值）。</summary>
    void SetCurrent(string? uid);

    /// <summary>删除账号（对照 logout 的 del users[uid]）。</summary>
    bool Remove(string uid);
}
