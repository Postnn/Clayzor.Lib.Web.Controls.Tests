using Clayzor.Lib.Web.Controls.Components.Grid.ColumnTypes;
using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты специальных типов вывода: Тип 8 (HTML), Тип 12 (ограниченный текст),
/// Тип 10/13 (дата/время локализованные — G14).
/// </summary>
public class SpecialColumnTypesTests
{
    /// <summary>Sanitize вырезает script-тег.</summary>
    [Fact]
    public void Sanitize_RemovesScriptTag()
    {
        var input = "<b>ok</b><script>alert(1)</script>";
        var result = ClayHtmlSanitizer.Sanitize(input);

        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sanitize сохраняет безопасный HTML.</summary>
    [Fact]
    public void Sanitize_PreservesSafeHtml()
    {
        var input  = "<b>ok</b>";
        var result = ClayHtmlSanitizer.Sanitize(input);
        Assert.Contains("<b>ok</b>", result);
    }

    /// <summary>Sanitize вырезает onclick-атрибут.</summary>
    [Fact]
    public void Sanitize_RemovesEventAttributes()
    {
        var input  = "<div onclick=\"alert(1)\">text</div>";
        var result = ClayHtmlSanitizer.Sanitize(input);
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sanitize вырезает javascript: схему.</summary>
    [Fact]
    public void Sanitize_RemovesJavascriptScheme()
    {
        var input  = "<a href=\"javascript:alert(1)\">link</a>";
        var result = ClayHtmlSanitizer.Sanitize(input);
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sanitize: null/пусто → без ошибок.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_NullOrEmpty_ReturnsSame(string? input)
    {
        var result = ClayHtmlSanitizer.Sanitize(input!);
        Assert.Equal(input ?? "", result);
    }

    /// <summary>Resolve(8) возвращает ClayHtmlColumnType.</summary>
    [Fact]
    public void Resolve_Type8_ReturnsHtmlDescriptor()
    {
        var desc = ClayColumnTypeMap.Resolve(8);
        Assert.NotNull(desc);
        Assert.IsType<ClayHtmlColumnType>(desc);
    }

    /// <summary>Resolve(12) возвращает ClayLimitedTextColumnType.</summary>
    [Fact]
    public void Resolve_Type12_ReturnsLimitedTextDescriptor()
    {
        var desc = ClayColumnTypeMap.Resolve(12);
        Assert.NotNull(desc);
        Assert.IsType<ClayLimitedTextColumnType>(desc);
    }
}
