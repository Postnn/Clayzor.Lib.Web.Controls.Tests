using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты валидатора имён параметров общей настройки <see cref="ClaySharedParamValidator"/>.
/// </summary>
public class ClaySharedParamValidatorTests
{
    private static readonly IReadOnlyList<string> GridParams =
        ["cols140", "flt140", "grp140", "srt140", "pgs140", "qks140"];

    /// <summary>Полное совпадение — валидно.</summary>
    [Fact]
    public void IsValid_AllMatch_ReturnsTrue()
    {
        var shared = new[] { "cols140", "flt140", "grp140", "srt140", "pgs140", "qks140" };
        Assert.True(ClaySharedParamValidator.IsValid(shared, GridParams));
    }

    /// <summary>Подмножество — валидно (пользователь не менял часть настроек).</summary>
    [Fact]
    public void IsValid_Subset_ReturnsTrue()
    {
        var shared = new[] { "cols140", "srt140" };
        Assert.True(ClaySharedParamValidator.IsValid(shared, GridParams));
    }

    /// <summary>Одно незнакомое имя — невалидно.</summary>
    [Fact]
    public void IsValid_ExtraName_ReturnsFalse()
    {
        var shared = new[] { "cols140", "unknown" };
        Assert.False(ClaySharedParamValidator.IsValid(shared, GridParams));
    }

    /// <summary>Смешанный набор (знакомые + одно незнакомое) — невалидно.</summary>
    [Fact]
    public void IsValid_MixedSet_ReturnsFalse()
    {
        var shared = new[] { "cols140", "flt140", "grp140", "srt140", "pgs140", "qks140", "other" };
        Assert.False(ClaySharedParamValidator.IsValid(shared, GridParams));
    }

    /// <summary>Регистр не влияет.</summary>
    [Fact]
    public void IsValid_CaseInsensitive()
    {
        var shared = new[] { "COLS140", "Flt140" };
        Assert.True(ClaySharedParamValidator.IsValid(shared, GridParams));
    }

    /// <summary>Пустой shared-набор — валиден (нет параметров — нет чужих имён).</summary>
    [Fact]
    public void IsValid_EmptySharedSet_ReturnsTrue()
    {
        Assert.True(ClaySharedParamValidator.IsValid([], GridParams));
    }

    /// <summary>Имя длиннее 20 символов в shared — невалидно (не принадлежит ни одному гриду).</summary>
    [Fact]
    public void IsValid_NameLongerThan20Chars_ReturnsFalse()
    {
        var shared = new[] { "cols140", "verylongprefix9999999" };
        Assert.False(ClaySharedParamValidator.IsValid(shared, GridParams));
    }
}
