using System.Runtime.CompilerServices;

// 内部 seam（JSON 读取辅助、socket 工厂等）向测试工程开放金丝雀断言。
[assembly: InternalsVisibleTo("BiliLiveTool.Tests")]
