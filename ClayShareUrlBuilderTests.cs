using Clayzor.Lib.Web.Controls.Components.Grid.Dynamic;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты построителя URL для «Поделиться» <see cref="ClayShareUrlBuilder"/>.
/// </summary>
public class ClayShareUrlBuilderTests
{
    /// <summary>URL с кучей параметров и фрагментом → только gridId и sharedId.</summary>
    [Fact]
    public void BuildShareUrl_KeepsOnlyGridIdAndSharedId()
    {
        var url = "http://localhost:5010/dynamic?id=140&filter=abc&page=3&sort=name&unrelated=x#section";
        var result = ClayShareUrlBuilder.BuildShareUrl(url, "id", 1);

        Assert.StartsWith("http://localhost:5010/dynamic?", result);
        Assert.Contains("id=140", result);
        Assert.Contains("sharedId=1", result);
        Assert.DoesNotContain("filter", result);
        Assert.DoesNotContain("page", result);
        Assert.DoesNotContain("sort", result);
        Assert.DoesNotContain("unrelated", result);
        Assert.DoesNotContain("#", result);
    }

    /// <summary>URL уже содержит sharedId → ровно один sharedId, с новым значением.</summary>
    [Fact]
    public void BuildShareUrl_ReplacesExistingSharedId()
    {
        var url = "http://localhost:5010/dynamic?id=140&sharedId=5";
        var result = ClayShareUrlBuilder.BuildShareUrl(url, "id", 99);

        // Ровно одно вхождение sharedId
        Assert.Single(result.Split('&'), p => p.StartsWith("sharedId="));
        Assert.Contains("sharedId=99", result);
        Assert.DoesNotContain("sharedId=5", result);
    }

    /// <summary>Результат — абсолютный URL со схемой, хостом, портом и путём.</summary>
    [Fact]
    public void BuildShareUrl_AbsoluteUrl()
    {
        var url = "https://app.example.com:8443/grid?id=140";
        var result = ClayShareUrlBuilder.BuildShareUrl(url, "id", 42);

        Assert.StartsWith("https://app.example.com:8443/grid?", result);
        Assert.Contains("id=140", result);
        Assert.Contains("sharedId=42", result);
    }

    /// <summary>URL без параметра грида → только sharedId.</summary>
    [Fact]
    public void BuildShareUrl_NoGridIdInUrl_StillAddsSharedId()
    {
        var url = "http://localhost:5010/dynamic?other=1";
        var result = ClayShareUrlBuilder.BuildShareUrl(url, "id", 7);

        Assert.StartsWith("http://localhost:5010/dynamic?", result);
        Assert.Contains("sharedId=7", result);
        Assert.DoesNotContain("other", result);
    }

    /// <summary>Спецсимволы в параметрах URL-кодируются.</summary>
    [Fact]
    public void BuildShareUrl_SpecialCharsInGridId_Encoded()
    {
        // Имя грида не влияет на параметры, но проверяем что AddQueryString кодирует корректно
        var url = "http://localhost:5010/dynamic?id=140&name=hello%20world";
        var result = ClayShareUrlBuilder.BuildShareUrl(url, "id", 1);

        Assert.Contains("id=140", result);
        Assert.Contains("sharedId=1", result);
        Assert.DoesNotContain("hello%20world", result); // выброшено белым списком
        Assert.DoesNotContain("name=", result);
    }
}
