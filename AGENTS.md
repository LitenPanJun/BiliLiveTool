# AGENTS.md

## 项目状态

- 这是 greenfield 仓库：**目前只有 `.refs/`（本地参考资料，已被 `.gitignore` 排除、不随仓库分发），没有任何源码、sln、csproj、CI**。第一步是按重构指南建 `BiliLiveTool.sln`（阶段 P0）。
- 不要猜测已有约定——本文件是唯一随仓库分发的依据；`.refs/` 仅在本地存在，若存在则为更详尽的权威参考。

## 权威参考（必须严格遵循，不得随意发挥）

按可信度排序，冲突时以更靠前者为准：

1. `.refs/docs/bilibili-live-stream-code-api-appendix.md` —— **API 冻结清单**（7 条）
2. `.refs/docs/bilibili_live_stream_code-refactor-guide.md` —— 分阶段计划、验收标准、关键技术决策
3. `.refs/docs/bilibili_live_stream_code-analysis.md` —— 8 维审计、命名规范、技术债务
4. `.refs/source-refs/` —— 原 Python 实现与 `dm.proto`（逐字段移植的对照源）

> 注意：以上文件不随仓库分发，仅原作者本地存在。二次开发若无 `.refs/`，仍须遵守下述硬规则与本文件其余约束；涉及具体 API 行为时应回到上游 [ChaceQC/bilibili_live_stream_code](https://github.com/ChaceQC/bilibili_live_stream_code) 核对，**不得自行推测或改写语义**。

硬规则：

- **API 冻结**：URL、HTTP 方法、参数名、签名排序与算法、字段映射一律不得改；任何变更需双人评审。签名基准值：`appkey=aae92bc66f3edfab` / `APP_SEC=af125a0d5279fd576c1b4418a3e8276d`、`mixinKeyEncTab` 64 项打乱表、`getMixinKey` 取前 32 字符、WBI 过滤 `!'()*`。
- **非目标**：不新增美颜推流、不改 B站逆向字段、不新增账号体系、不新增附录之外的接口。
- 参考文件只放在 `.refs/source-refs/` 单一目录，工作区不另建副本目录。

## 工程结构与命名（C# 强制）

Solution `BiliLiveTool`，5 工程，`net10.0`，命名空间与目录一一对应：

| 工程 | 对应原 Python | 职责边界 |
|---|---|---|
| `BiliLiveTool.App` | `main.py` | Avalonia 启动、托盘、生命周期 |
| `BiliLiveTool.Core` | `state.py` / `data.py` | 仅 DTO、`record`、interface，**禁止依赖 HTTP** |
| `BiliLiveTool.Infrastructure` | `bilibili_api.py` / `get_wbi.py` / `dm_pb2.py` | `BilibiliApiClient` / `WbiSigner` / `AppSigner` / `DanmuPacketCodec` / `SecretMasker` |
| `BiliLiveTool.Services` | `services/*` | `AuthService` / `UserService` / `LiveService` / `DanmuService` / `AuthSessionStore` |
| `BiliLiveTool.UI` | `frontend/src` | Views + ViewModels |

- PascalCase；类型名用名词短语；接口加 `I` 前缀；异步方法加 `Async` 后缀。
- 产品标识固定 `BiliLiveTool`（配置路径沿用 `~/.config/BiliLiveTool`），**弃用 `BiliLiveStudio`**。
- 对象约束：`sealed class + interface` 划边界；构造函数注入（`services.AddSingleton<T>()` / `AddHttpClient<,>`）；状态用不可变 `record` 快照传递；`SessionState` 禁止公开 setter，可变会话集中在 `AuthSessionStore`；无公开可变静态；`Infrastructure` 内签名与编解码类为 `internal sealed`。
- 禁止跨层调用私有成员（如 Service 直调 `_req`），一律经 `BilibiliApiClient` 公有领域方法。

## 构建与验证

- `Directory.Build.props` 必须开启 `Nullable=enable`、`TreatWarningsAsErrors=true`、`EnforceCodeStyleInBuild=true`。
- **版本管理（一次性全局修改）**：版本唯一来源是根目录 `VERSION`（格式 `major.minor.patch.yymmdd`，如 `0.2.0.261002` 对应 2026-10-02 的提交）；`Directory.Build.props` 读取它并派生 `Version` / `AssemblyVersion` / `FileVersion` / `Product` / `Company` / `Copyright` / `Description`。升版**只改 `VERSION` 一行**（与对应提交同笔提交），全部输出 exe/dll 的文件属性与 `FileVersionInfo` 详情自动同步；完整版本（可含 `+git sha`）见 `InformationalVersion`（即文件属性的 Product version）。注意程序集版本每段 16 位（≤65535），超限的 yymmdd 段由 props 截去、派生 `x.y.z.0`；`BiliLiveTool.Tests/Build/VersionTests` 封死格式违约与各工程版本漂移。
- 依赖统一在 `Directory.Packages.props` 钉版（`Google.Protobuf` / `Avalonia` / `CommunityToolkit.Mvvm` / `Microsoft.Extensions.*`）。
- 测试框架 xUnit（+ FluentAssertions）。命令：`dotnet build`、`dotnet test`；单个测试用 `dotnet test --filter "FullyQualifiedName~<名字>"`。
- 必测纯逻辑：`WbiSigner` / `AppSigner` / `DanmuPacketCodec` / `SecretMasker` 行覆盖率 ≥ 90%，用录制样本做金丝雀断言（与 Python 输出逐字节一致）。
- 生成代码：`dm.proto` 用 `Google.Protobuf` 生成 `InteractWordV2`，**勿换 protobuf-net**（字段序号会漂移）。

## 安全硬约束（验收门槛）

- `SESSDATA` / `bili_jct` / `buvid3` 必须封装为 `SecureCredential`，禁止 `string` 裸传、禁止 `ToString` 或序列化落盘；持久化走系统钥匙串，内存用完 `Dispose` 清零。
- 非密钥配置才落 `config.json`，Linux 目录 `chmod 700`、文件 `chmod 600`。
- 日志唯一出口是 `ISecretMasker`：`rtmp addr/code/token/SESSDATA` 严禁原文输出。
- `HttpClient` 仅 HTTPS + 证书校验，超时 10s，Header 逐项复刻附录第 1 节。

## 行为语义要点（易做错）

- 扫码登录轮询 1500ms；过期 `86038`、已扫 `86090`；poll 身份取自 `Set-Cookie`。
- `startLive` 三步 `now / liveVersionInfo / startLive`；`60024` 取 `data.qr`、`60043` 自拼人脸 URL（含 `mid`），原样返回不吞。
- 弹幕发送 `msg/send` 的 `bullet_data{color:16777215,fontsize:25,mode:1,bubble:0}` + `web_location:444.8`，错误码映射 `0/1003212/-101/-400/10031`。
- WS：`getDanmuInfo{id,type:0}` 取 `host_list[0]`，`op=7` 认证（`protover:3`，未登录 `uid=0`）、`op=2` 心跳 30s、`op=3` 人气、`proto 2=zlib / 3=brotli` 递归解包；六类 `cmd` 字段映射见附录 6。
- WBI Key 缓存 24 小时；重连指数退避 + jitter + 熔断；未知 `cmd` 记 debug 不静默丢弃。
- 切账号/登出必须先 `await StopAsync` 再切换，失败回滚旧快照（禁止 fire-and-forget）。
- 退出 3 秒内干净注销，无残留进程。

## 实施顺序

严格按重构指南 P0 → P1 → P2：P0 安全与骨架（签名、`SecureCredential`、`BilibiliApiClient`、金丝雀单测）→ P1 服务与弹幕核心 → P2 Avalonia 界面与发布。不要跳阶段先写 UI。

## Git 提交约定

- **未经用户明确允许，禁止 `git push`**（含任何远端、含 `--force`）：提交只落本地，推送必须由用户明确指示后才执行。
- 颗粒度：**line > file > feat** —— 尽量小步提交，一次提交聚焦单点改动/单个文件，避免大块 feature 提交。
- 摘要行：以**标准英文类型前缀**开头（Conventional Commits：`feat` / `fix` / `docs` / `chore` / `refactor` / `test` / `perf` / `build` / `ci`），可用时在类型后加 `(...)` 括号注解范围，如 `docs(README): ...`。
- 摘要其余部分与正文说明**用中文**：祈使句、首行 ≤ 72 字符、正文解释"为什么"。
- **署名唯一**：author/committer 一律 `Zachary Pan <xiaopanjun233@163.com>`；正文禁止 `Co-Authored-By:` / `Authored-by:` 等一切合著或 AI 署名 trailer——GitHub 会把 `noreply@anthropic.com` 解析成 claude 账号，在提交页显示为「Claude」贡献者。`.githooks/commit-msg` 会拒绝此类提交（经 `git config core.hooksPath .githooks` 启用，勿删）。
