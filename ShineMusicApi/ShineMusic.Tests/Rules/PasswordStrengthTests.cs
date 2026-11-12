using ShineMusic.Service;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>前后端各有一份实现，后端这份必须与 utils/password-strength.ts 保持同一口径。</summary>
public sealed class PasswordStrengthTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("Ab1!", 1)]
    [InlineData("abcdefg", 0)]
    public void Short_passwords_never_exceed_level_one(string? password, int expected)
    {
        Assert.Equal(expected, PasswordStrength.Estimate(password));
    }

    [Theory]
    [InlineData("abcdefgh", 1)]
    [InlineData("Abcdefgh", 2)]
    [InlineData("Abcdefg1", 3)]
    [InlineData("Abcdefg1!", 4)]
    [InlineData("Abcdefghijk1!", 4)]
    public void Length_and_character_classes_compose(string password, int expected)
    {
        Assert.Equal(expected, PasswordStrength.Estimate(password));
    }

    [Fact]
    public void Result_is_clamped_to_max_level()
    {
        var level = PasswordStrength.Estimate("Abcdefghijklmnop1!@#$");
        Assert.InRange(level, 0, PasswordStrength.MaxLevel);
    }
}
