using System.Runtime.CompilerServices;

// 组合根依赖图向测试工程开放：ValidateOnBuild 单测封死
// 漏注册导致的启动崩溃回归（曾以未处理异常 exit 134 收场）。
[assembly: InternalsVisibleTo("BiliLiveTool.Tests")]
