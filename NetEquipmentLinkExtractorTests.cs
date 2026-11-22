using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Golden-проверки legacy-алгоритма извлечения ссылок порта.</summary>
public sealed class NetEquipmentLinkExtractorTests
{
    /// <summary>R01: пустые значения не создают кандидатов.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public void EmptyText_HasNoTokens(string? text) => Assert.Empty(NetEquipmentLinkExtractor.Extract(text));

    /// <summary>R02–R03: имена, дефис, подчёркивание и FQDN сохраняют legacy-форму.</summary>
    [Theory]
    [InlineData("server01", "server01")]
    [InlineData("srv-01 srv_name server.example.org", "srv-01|srv_name|server.example.org")]
    public void Names_UseLegacyRegex(string text, string expected) =>
        Assert.Equal(expected.Split('|'), NetEquipmentLinkExtractor.Extract(text));

    /// <summary>R04–R05: преобразование ilo- зависит от регистра и заменяет все вхождения.</summary>
    [Theory]
    [InlineData("ilo-server01", "server01")]
    [InlineData("ILO-server01", "ILO-server01")]
    [InlineData("ilo-ilo-server01", "server01")]
    public void IloPrefix_MatchesLegacy(string text, string expected) =>
        Assert.Equal(expected, Assert.Single(NetEquipmentLinkExtractor.Extract(text)));

    /// <summary>R06: доменный разделитель остаётся источником отдельных кандидатов.</summary>
    [Theory]
    [InlineData("DOMAIN\\SERVER", "DOMAIN|SERVER")]
    [InlineData("corp\\server01", "corp|server01")]
    public void DomainNames_AreNotNormalized(string text, string expected) =>
        Assert.Equal(expected.Split('|'), NetEquipmentLinkExtractor.Extract(text));

    /// <summary>R07: MAC добавляется после текстовых фрагментов без нормализации разделителей.</summary>
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA|BB|CC|DD|EE|FF|AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF", "AABBCCDDEEFF")]
    [InlineData("AA.BB.CC.DD.EE.FF", "AA.BB.CC.DD.EE.FF")]
    public void MacAddresses_KeepLegacyDoubleDetection(string text, string expected) =>
        Assert.Equal(expected.Split('|'), NetEquipmentLinkExtractor.Extract(text));

    /// <summary>R09: IP не превращается в отдельный IP-поиск.</summary>
    [Fact]
    public void IpAddress_UsesOnlyLegacyRegex() =>
        Assert.Equal(["20"], NetEquipmentLinkExtractor.Extract("192.168.1.20"));

    /// <summary>R10/R20: кириллица и HTML не становятся разметкой или произвольными кандидатами.</summary>
    [Fact]
    public void HtmlAndCyrillic_ExtractOnlyLegacyLatinFragments() =>
        Assert.Equal(["script", "alert", "x", "*", "DROP", "TABLE"],
            NetEquipmentLinkExtractor.Extract("Привет <script>alert('x')</script> * ; DROP TABLE"));

    /// <summary>R11: дедупликация учитывает регистр и порядок первого появления.</summary>
    [Fact]
    public void DuplicateNames_AreCaseSensitiveAndStable() =>
        Assert.Equal(["Server01", "server01", "SERVER01"],
            NetEquipmentLinkExtractor.Extract("Server01 server01 SERVER01 server01"));

    /// <summary>R13: все текстовые кандидаты предшествуют новым MAC второго прохода.</summary>
    [Fact]
    public void MixedMultilineText_PreservesTwoPassOrder() =>
        Assert.Equal(["srv-a", "AA", "BB", "CC", "DD", "EE", "FF", "srv-b", "AA:BB:CC:DD:EE:FF"],
            NetEquipmentLinkExtractor.Extract("srv-a AA:BB:CC:DD:EE:FF\r\nsrv-b"));

    /// <summary>R17: длинный текст обрабатывается без разбиения на символы.</summary>
    [Fact]
    public void LongText_ReturnsUniqueTokens()
    {
        var text = string.Join(' ', Enumerable.Range(0, 2000).Select(index => $"srv{index}"));
        var tokens = NetEquipmentLinkExtractor.Extract(text);
        Assert.Equal(2000, tokens.Count);
        Assert.Equal("srv0", tokens[0]);
        Assert.Equal("srv1999", tokens[^1]);
    }
}
