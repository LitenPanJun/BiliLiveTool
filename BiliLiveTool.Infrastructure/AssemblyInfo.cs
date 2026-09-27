using System.Runtime.CompilerServices;

// 签名、编解码与脱敏类按规范保持 internal sealed，
// 仅向测试工程开放金丝雀断言。
[assembly: InternalsVisibleTo("BiliLiveTool.Tests")]
