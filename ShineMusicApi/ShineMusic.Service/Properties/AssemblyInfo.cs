using System.Runtime.CompilerServices;

// 允许 ShineMusic.Tests 访问 internal 匹配逻辑（TitleMatches / NormalizeTitle 等），
// 以便对「曲目身份匹配」这一正确性闸门做单元锁定。
[assembly: InternalsVisibleTo("ShineMusic.Tests")]
