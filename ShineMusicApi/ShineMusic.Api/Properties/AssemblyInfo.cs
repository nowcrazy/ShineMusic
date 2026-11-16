// 顶层语句生成的 Program 类型是 internal；L3a 集成测试需要以 WebApplicationFactory<Program>
// 引用它作为入口点程序集标记，因此给测试项目授予友元程序集访问权。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ShineMusic.Tests")]
