using System.Runtime.CompilerServices;

// 内部日志汇聚 seam（MaskLine 脱敏、有界通道）向测试工程开放断言，
// 保证读取侧永不见原文。
[assembly: InternalsVisibleTo("BiliLiveTool.Tests")]
