using System.Runtime.CompilerServices;

// SecureCredential.DangerousGetValue 保持 internal（指南要求），
// 仅供 Infrastructure 装配 Cookie 与测试金丝雀断言在受控边界读取。
[assembly: InternalsVisibleTo("BiliLiveTool.Infrastructure")]
[assembly: InternalsVisibleTo("BiliLiveTool.Tests")]
