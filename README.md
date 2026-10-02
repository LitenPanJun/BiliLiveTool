# BiliLiveTool

> **AI 辅助编程声明**：本项目使用模型 Xiaomi Mimo V2.6 Flash 辅助开发。

B站直播伴侣桌面工具：扫码登录与多账号管理、分区/标题/公告修改、开播取推流码（RTMP/SRT，含人脸验证分支）、弹幕收发监控。

## 功能特性

- 扫码登录，多账号切换
- 分区、直播间标题与公告修改
- 开播/停播，取双路 RTMP 与 SRT 推流码（含 `60024`/`60043` 人脸验证分支）
- 弹幕接收六类消息渲染与弹幕发送
- 托盘驻留、日志控制台；目标平台 Windows 10+ 与 Ubuntu 22.04+

## 使用

### 获取

GitHub Actions 的 `BiliLiveTool-win-x64` / `BiliLiveTool-linux-x64` 产物为自包含发布包，解压即可运行，无需安装 .NET 运行时。

```bash
./BiliLiveTool          # Linux
BiliLiveTool.exe        # Windows
```

### 功能页面

- **账号**：生成/刷新二维码扫码登录（1.5 秒轮询，过期自动提示重取码），支持多账号切换；每个账号的登录态独立保存。
- **直播**：修改分区、直播间标题与公告，开播/停播；双路 RTMP 与 SRT 推流码只经「复制地址」「复制码」按钮出栈，不可选中；触发人脸验证（`60024`/`60043`）时弹出仅可复制的验证链接。
- **弹幕**：实时渲染六类消息（弹幕、进场、礼物、连击、互动等），支持发送弹幕；消息风暴自动限流，不卡界面。
- **控制台**：运行日志实时输出，写入即自动脱敏——推流码、登录凭据不会以原文出现。
- **托盘**：菜单含「显示主窗口」「退出」；关闭窗口默认最小化到托盘（`min_to_tray`，默认开启）；经托盘退出时 3 秒内完成清理，无残留进程。

### 数据与安全

- 配置与账号资料存于 `~/.config/BiliLiveTool/config.json`（遵循 `XDG_CONFIG_HOME`；目录权限 700、文件 600，**仅存非密钥偏好**，如 `min_to_tray`、上次分区与标题）。
- `SESSDATA`、`bili_jct` 等机密保存在系统钥匙串（Windows 凭据管理器 / macOS Keychain / Linux Secret Service），不以明文落盘；钥匙串不可用时降级为仅本次会话内存，重启需重新扫码。
- 检测到旧版含明文机密的配置时，自动备份为 `config.json.bak`，只迁移非密钥字段并要求重新扫码。

## 技术栈与工程结构

.NET 10 LTS + Avalonia UI + CommunityToolkit.Mvvm，Solution 名 `BiliLiveTool`，5 个工程：

| 工程 | 职责 |
|---|---|
| `BiliLiveTool.App` | 启动、托盘、生命周期 |
| `BiliLiveTool.Core` | DTO、`record`、interface（禁止依赖 HTTP） |
| `BiliLiveTool.Infrastructure` | B站 API 封装、WBI/APP 签名、弹幕编解码 |
| `BiliLiveTool.Services` | 登录、用户、直播、弹幕服务与会话存储 |
| `BiliLiveTool.UI` | Views 与 ViewModels |

## 构建与测试

需要 .NET 10 SDK：

```bash
dotnet build
dotnet test                                               # 全量测试
dotnet test --filter "FullyQualifiedName~<名字>"           # 单个测试
dotnet publish BiliLiveTool.App/BiliLiveTool.App.csproj \
  -c Release -r linux-x64 --self-contained true           # 自包含发布（win-x64 同理）
```

工程约束已内建于构建配置：`Nullable=enable`、`TreatWarningsAsErrors=true`（0 警 0 错）、依赖集中钉版于 `Directory.Packages.props`、版本号单源于 `Directory.Build.props`。

## 贡献

欢迎 Issue 与 Pull Request。提交前请遵循：

1. **小步提交**：颗粒度「行 > 文件 > 特性」，一次提交聚焦单点改动，保证每笔提交都可构建。
2. **提交信息**：使用 Conventional Commits 英文类型前缀（`feat`/`fix`/`docs`/`chore`/`refactor`/`test`/`perf`/`build`/`ci`，可加 `(...)` 括注范围）；摘要其余部分与正文用中文祈使句，首行 ≤ 72 字符，正文解释「为什么」。
3. **质量门禁**：提交前 `dotnet build` 与 `dotnet test` 必须全绿；新增功能补单测，签名、编解码、脱敏等纯逻辑保持高测试覆盖率。
4. **API 冻结**：B站接口的 URL、HTTP 方法、参数名、签名算法与字段映射必须与上游保持一致，不新增附录外接口、不改逆向字段；若 B站侧发生变更，请先开 Issue 讨论。
5. **安全红线**：机密一律经 `SecureCredential` 与系统钥匙串，禁止明文落盘或写入日志；不要提交任何真实凭据（`config.json` 已在 `.gitignore` 中）。

## 开发说明

- **上游项目**：基于 [ChaceQC/bilibili_live_stream_code](https://github.com/ChaceQC/bilibili_live_stream_code) 重构，B站 API 语义与上游保持一致，不新增接口、不改字段映射。

## 许可证

[GPL-3.0](LICENSE)
