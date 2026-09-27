# BiliLiveTool

B站直播伴侣桌面工具：扫码登录与多账号管理、分区/标题/公告修改、开播取推流码（RTMP/SRT，含人脸验证分支）、弹幕收发监控。

## 项目状态

Greenfield 阶段：**尚无 .NET 源码**，按 P0（安全与骨架）→ P1（服务与弹幕核心）→ P2（界面与发布）分阶段推进。协作约定见 [AGENTS.md](AGENTS.md)。

## 功能特性

- 扫码登录，多账号切换
- 分区、直播间标题与公告修改
- 开播/停播，取双路 RTMP 与 SRT 推流码（含 `60024`/`60043` 人脸验证分支）
- 弹幕接收六类消息渲染与弹幕发送
- 托盘驻留、日志控制台；目标平台 Windows 10+ 与 Ubuntu 22.04+

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

Solution 建立后使用：

```bash
dotnet build
dotnet test                                               # 全量测试
dotnet test --filter "FullyQualifiedName~<名字>"           # 单个测试
```

工程约束（`Nullable`、`TreatWarningsAsErrors`、依赖钉版等）见 [AGENTS.md](AGENTS.md)。

## 开发说明

- **AI 编程辅助**：本项目使用模型 Xiaomi Mimo V2.6 Flash 辅助开发。
- **上游项目**：基于 [ChaceQC/bilibili_live_stream_code](https://github.com/ChaceQC/bilibili_live_stream_code) 重构，B站 API 语义与上游保持一致，不新增接口、不改字段映射。

## 许可证

[GPL-3.0](LICENSE)
