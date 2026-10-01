using System.Runtime.CompilerServices;

// 签名、编解码与脱敏类按规范保持 internal sealed，
// 向服务层（帧编解码消费方）、应用组合根（SecretMasker 装配）
// 与测试工程开放。
[assembly: InternalsVisibleTo("BiliLiveTool.Services")]
[assembly: InternalsVisibleTo("BiliLiveTool.App")]
[assembly: InternalsVisibleTo("BiliLiveTool.Tests")]
