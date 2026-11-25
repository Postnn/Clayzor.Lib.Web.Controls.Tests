using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class MultipleSelectionBridgeTests
{
    [Theory]
    [InlineData("[]", "")]
    [InlineData("[107,7,107]", "7,107")]
    public async Task ReturnsConfirmedSet(string json, string expected)
    {
        var js = new Probe();
        await using var service = new ClaySelectAdvancedSearchService(js);
        var task = service.OpenMultipleIdsAsync("https://example.invalid/tree");
        using var document = JsonDocument.Parse(json);
        await service.CompleteAsync(js.Request, document.RootElement);
        Assert.Equal(expected, string.Join(",", (await task)!));
        Assert.True(js.Multiple);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("[0]")]
    [InlineData("[-1]")]
    [InlineData("[\"17\"]")]
    public async Task RejectsMalformedSelection(string json)
    {
        var js = new Probe();
        await using var service = new ClaySelectAdvancedSearchService(js);
        var task = service.OpenMultipleIdsAsync("https://example.invalid/tree");
        using var document = JsonDocument.Parse(json);
        await service.CompleteAsync(js.Request, document.RootElement);
        await Assert.ThrowsAsync<JsonException>(async () => await task);
    }

    [Fact]
    public async Task CancelDoesNotClearSelection()
    {
        var js = new Probe();
        await using var service = new ClaySelectAdvancedSearchService(js);
        var task = service.OpenMultipleIdsAsync("https://example.invalid/tree");
        await service.CancelAsync(js.Request);
        Assert.Null(await task);
    }

    private sealed class Probe : IJSRuntime
    {
        public string Request { get; private set; } = "";
        public bool Multiple { get; private set; }

        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args)
        {
            if (name == "claySelectAdvancedSearch.open")
            {
                Request = (string)args![1]!;
                Multiple = (bool)args[3]!;
            }
            return ValueTask.FromResult(default(T)!);
        }
    }
}
