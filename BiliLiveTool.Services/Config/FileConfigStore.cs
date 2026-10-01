using System.Text.Json;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Config;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Services.Config;

/// <summary>
/// config.json 落盘存储：账号注册表与应用偏好共用单文件（对照原
/// config_manager）。仅非密钥落盘（机密一律系统钥匙串），Linux/macOS
/// 目录 700、文件 600，临时文件原子替换；启动检测到旧格式（含明文
/// 机密）先备份 config.json.bak，仅按属性名白名单迁移，机密一律丢弃
/// 要求重扫码（对照指南迁移策略）。落盘尽力而为，失败不阻断会话。
/// </summary>
public sealed class FileConfigStore : IAccountStore, IAppConfigStore
{
    internal const int SchemaVersion = 2;

    private const string FileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<FileConfigStore> _log;
    private readonly Dictionary<string, AccountRecord> _accounts = new(StringComparer.Ordinal);
    private string? _currentUid;
    private bool _minToTray = true;

    public FileConfigStore(ILogger<FileConfigStore> log)
        : this(DefaultPath(), log)
    {
    }

    /// <summary>测试注入口：指定 config.json 路径。</summary>
    internal FileConfigStore(string path, ILogger<FileConfigStore> log)
    {
        _path = path;
        _log = log;
        try
        {
            LoadOrMigrate();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // 启动尽力装载：失败即用默认骨架，后续写入会重建文件
            _log.LogWarning(e, "Load config failed, starting with defaults");
        }
    }

    // --- IAccountStore ---

    public string? CurrentUid
    {
        get
        {
            lock (_gate)
            {
                return _currentUid;
            }
        }
    }

    public AccountRecord? Get(string uid)
    {
        lock (_gate)
        {
            return _accounts.GetValueOrDefault(uid);
        }
    }

    public IReadOnlyList<AccountRecord> List()
    {
        lock (_gate)
        {
            return _accounts.Values.ToList();
        }
    }

    public void Save(AccountRecord account)
    {
        lock (_gate)
        {
            _accounts[account.Uid] = account;
            _currentUid = account.Uid;
            PersistLocked();
        }
    }

    public void Upsert(AccountRecord account)
    {
        lock (_gate)
        {
            _accounts[account.Uid] = account;
            PersistLocked();
        }
    }

    public void SetCurrent(string? uid)
    {
        lock (_gate)
        {
            _currentUid = uid;
            PersistLocked();
        }
    }

    public bool Remove(string uid)
    {
        lock (_gate)
        {
            if (!_accounts.Remove(uid))
                return false;
            if (_currentUid == uid)
                _currentUid = null;
            PersistLocked();
            return true;
        }
    }

    // --- IAppConfigStore ---

    public bool MinToTray
    {
        get
        {
            lock (_gate)
            {
                return _minToTray;
            }
        }
    }

    public void SetMinToTray(bool value)
    {
        lock (_gate)
        {
            _minToTray = value;
            PersistLocked();
        }
    }

    // --- 装载与迁移 ---

    private void LoadOrMigrate()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        TightenDirectory(dir);

        if (!File.Exists(_path))
        {
            lock (_gate)
            {
                PersistLocked();
            }

            return;
        }

        var text = File.ReadAllText(_path);
        lock (_gate)
        {
            if (!TryLoadOwnFormat(text) && !TryMigrateLegacy(text))
            {
                // 损坏文件：备份原样后重置，.bak 保住现场
                BackupLocked();
            }

            PersistLocked(); // 规范化写回并收紧权限
        }
    }

    private bool TryLoadOwnFormat(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root)
                return false;
            if (root["schema"] is not JsonValue schemaValue
                || !schemaValue.TryGetValue<int>(out var schema)
                || schema != SchemaVersion)
                return false;

            HydrateLocked(root);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryMigrateLegacy(string text)
    {
        JsonObject root;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed)
                return false;
            root = parsed;
        }
        catch (JsonException)
        {
            return false;
        }

        BackupLocked();
        _accounts.Clear();

        _minToTray = root["min_to_tray"] is JsonValue tray && tray.TryGetValue<bool>(out var trayValue)
            ? trayValue
            : true;
        _currentUid = ReadText(root["current_uid"]);

        switch (root["users"])
        {
            case JsonObject users:
            {
                foreach (var (uid, node) in users)
                    TryAddLegacy(node, uid);
                break;
            }
            case JsonArray list:
            {
                foreach (var node in list)
                    TryAddLegacy(node, null);
                break;
            }
        }

        _log.LogInformation(
            "Migrated legacy config: {Count} account(s), secrets dropped (rescan required)",
            _accounts.Count);
        return true;
    }

    private void TryAddLegacy(JsonNode? node, string? fallbackUid)
    {
        if (node is not JsonObject source)
            return;

        var filtered = FilterLegacy(source, fallbackUid);
        if (filtered is null)
            return;

        try
        {
            var record = JsonSerializer.Deserialize<AccountRecord>(filtered, JsonOptions);
            if (record is { Uid: not "" })
                _accounts[record.Uid] = record;
        }
        catch (JsonException)
        {
            // 单条损坏跳过，备份仍在
        }
    }

    /// <summary>
    /// 属性名白名单过滤（规范化匹配，兼容 last_title / LastTitle 等风格）；
    /// 白名单外的键（cookies、SESSDATA 等机密）一律丢弃。
    /// </summary>
    private static JsonObject? FilterLegacy(JsonObject source, string? fallbackUid)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in typeof(AccountRecord).GetProperties())
            map[Normalize(property.Name)] = property.Name;

        var target = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (!map.TryGetValue(Normalize(key), out var propertyName))
                continue; // 非白名单：含一切机密字段
            target[propertyName] = value?.DeepClone();
        }

        if (target[nameof(AccountRecord.Uid)] is null)
        {
            if (fallbackUid is null)
                return null;
            target[nameof(AccountRecord.Uid)] = fallbackUid;
        }

        return target;
    }

    private void HydrateLocked(JsonObject root)
    {
        _minToTray = root["min_to_tray"] is JsonValue tray && tray.TryGetValue<bool>(out var trayValue)
            ? trayValue
            : true;
        _currentUid = ReadText(root["current_uid"]);

        _accounts.Clear();
        if (root["users"] is not JsonObject users)
            return;

        foreach (var (uid, node) in users)
        {
            if (node is not JsonObject obj)
                continue;
            try
            {
                var record = JsonSerializer.Deserialize<AccountRecord>(obj, JsonOptions);
                if (record is { Uid: not "" })
                    _accounts[record.Uid] = record;
            }
            catch (JsonException)
            {
                // 单条损坏跳过
            }
        }
    }

    private void BackupLocked()
    {
        try
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
            TightenFile(_path + ".bak");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Config backup failed");
        }
    }

    // --- 持久化 ---

    private void PersistLocked()
    {
        try
        {
            var users = new JsonObject();
            foreach (var (uid, record) in _accounts)
                users[uid] = JsonSerializer.SerializeToNode(record, JsonOptions);

            var root = new JsonObject
            {
                ["schema"] = SchemaVersion,
                ["min_to_tray"] = _minToTray,
                ["current_uid"] = _currentUid is null ? null : JsonValue.Create(_currentUid),
                ["users"] = users,
            };

            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            TightenDirectory(dir);

            // 临时文件先收紧权限再原子替换，杜绝 644 窗口驻留
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(JsonOptions));
            TightenFile(tmp);
            File.Move(tmp, _path, overwrite: true);
            TightenFile(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 尽力落盘：磁盘故障不阻断会话（内存态继续工作）
            _log.LogWarning(e, "Persist config failed");
        }
    }

    private void TightenDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            _log.LogWarning(e, "Cannot chmod config directory: {Dir}", dir);
        }
    }

    private void TightenFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            _log.LogWarning(e, "Cannot chmod config file: {Path}", path);
        }
    }

    private static string? ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string DefaultPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        return Path.Combine(baseDir, "BiliLiveTool", FileName);
    }
}
