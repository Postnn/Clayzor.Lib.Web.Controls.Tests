using Clayzor.Lib.Web.Controls.Themes;
using MudBlazor.Utilities;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки соответствия общей темы подтверждённым токенам Kimi Design.</summary>
public sealed class ClayThemeTests
{
    /// <summary>Светлая палитра использует цвета из страницы токенов design-файла.</summary>
    [Fact]
    public void LightPalette_UsesDesignTokens()
    {
        var theme = ClayTheme.Create();

        Assert.Equal("#05164D", ClayColors.Navy);
        Assert.Equal("#000245", ClayColors.NavyDark);
        Assert.Equal("#52627C", ClayColors.NavyMid);
        Assert.Equal("#FFAD00", ClayColors.Gold);
        Assert.Equal("#F5F5F5", ClayColors.OffWhite);
        Assert.Equal("#EBEBEB", ClayColors.GreyLight);
        Assert.Equal(ClayColors.Navy, Hex(theme.PaletteLight.TextPrimary), ignoreCase: true);
        Assert.Equal(ClayColors.NavyMid, Hex(theme.PaletteLight.TextSecondary), ignoreCase: true);
        Assert.Equal(ClayColors.OffWhite, Hex(theme.PaletteLight.Background), ignoreCase: true);
        Assert.Equal(ClayColors.GreyLight, Hex(theme.PaletteLight.LinesDefault), ignoreCase: true);
    }

    /// <summary>Типографика и радиусы соответствуют design-файлу без внешних шрифтов.</summary>
    [Fact]
    public void TypographyAndRadius_UseDesignTokens()
    {
        var theme = ClayTheme.Create();

        Assert.Equal("2.25rem", theme.Typography.H4.FontSize);
        Assert.Equal("300", theme.Typography.H4.FontWeight);
        Assert.Equal("1.5rem", theme.Typography.H5.FontSize);
        Assert.Equal("700", theme.Typography.H5.FontWeight);
        Assert.Equal("var(--clay-font-size)", theme.Typography.Default.FontSize);
        Assert.Equal("var(--clay-font-size-secondary)", theme.Typography.Caption.FontSize);
        Assert.Equal("var(--clay-font-size)", theme.Typography.Button.FontSize);
        Assert.Equal("700", theme.Typography.Button.FontWeight);
        Assert.Equal("none", theme.Typography.Button.TextTransform);
        Assert.Equal("var(--clay-radius)", theme.LayoutProperties.DefaultBorderRadius);
    }

    private static string Hex(MudColor color) => color.ToString(MudColorOutputFormats.Hex);
}
