// =============================================================================
// 文件：Services/PasswordStrength.cs
// 用途：密码强度评分（0-4 级），用于「修改密码」环节的强度门槛校验与前端强度条展示。
//
// 评分口径（前后端必须一致，前端 utils/password-strength.ts 有一份对等实现）：
//   - 长度 < 8：即便字符种类多也视为不安全，最多 1 级；
//   - 长度 8~11 加 1 分，长度 >=12 加 2 分；
//   - 字符种类（小写/大写/数字/特殊）每多一种额外加分，封顶 4 级。
//
// 刻意不做在线词典/撞库等重型强度评估，保持纯本地、确定性、零外部依赖，
// 保证前端实时强度条与后端门槛判定结果一致。
// =============================================================================
namespace ShineMusic.Service;

/// <summary>密码强度评分器。</summary>
public static class PasswordStrength
{
    /// <summary>最高强度等级。</summary>
    public const int MaxLevel = 4;

    /// <summary>
    /// 计算密码强度等级（0-4，越大越强）。
    /// </summary>
    /// <param name="password">待评估的密码明文。</param>
    /// <returns>0（非常弱）到 4（极强）之间的整数。</returns>
    public static int Estimate(string? password)
    {
        var pwd = password ?? string.Empty;

        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasSpecial = false;
        foreach (var c in pwd)
        {
            if (char.IsLower(c)) hasLower = true;
            else if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSpecial = true;
        }

        var classes = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);

        // 长度过短：长度本身已构成风险，最多给到 1 级。
        if (pwd.Length < 8)
            return classes >= 3 ? 1 : 0;

        var score = pwd.Length >= 12 ? 2 : 1; // 长度贡献
        score += classes - 1;                  // 字符种类贡献（1 类不加分，4 类加 3 分）
        return Math.Clamp(score, 0, MaxLevel);
    }
}
