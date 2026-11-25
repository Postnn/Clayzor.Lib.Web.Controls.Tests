using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class FinderTests
{
    [Fact]
    public void ExclusionIsByEquipmentAcrossConnections()
    {
        var (sql, args) = PhoneFinderData.BuildEquipmentQuery(new(200, 0, null, 72, null), [17, 17, 22]);
        Assert.Contains("F.КодОборудования <> @ExcludedEquipment0", sql);
        Assert.Contains("F.КодОборудования <> @ExcludedEquipment1", sql);
        Assert.Equal(17, args.Get<int>("ExcludedEquipment0"));
        Assert.Equal(22, args.Get<int>("ExcludedEquipment1"));
        Assert.DoesNotContain("@ExcludedEquipment2", sql);
    }

    [Fact]
    public async Task RoundTripsAnExactCompositeKeyWithoutSharingRequests()
    {
        var js = new FinderInterop();
        await using var service = new ClaySelectAdvancedSearchService(js);
        var equipment = service.OpenAsync<PhoneEquipmentLookup, PhoneEquipmentKey>("http://localhost/Default/Finder");
        var phone = service.OpenAsync<PhoneNumberLookup, int?>("http://localhost/Default/FinderTel");
        Assert.Equal(2, js.Opened.Count);
        Assert.NotEqual(js.Opened[0], js.Opened[1]);
        await service.CompleteAsync(js.Opened[0], JsonSerializer.SerializeToElement(new { equipmentId = 17, connectionEquipmentId = 39 }));
        await service.CompleteAsync(js.Opened[0], JsonSerializer.SerializeToElement(new { equipmentId = 17, connectionEquipmentId = 40 }));
        Assert.Equal(new PhoneEquipmentKey(17, 39), (await equipment)!.Value);
        Assert.False(phone.IsCompleted);
        await service.CancelAsync(js.Opened[1]);
        Assert.Null(await phone);
        Assert.Equal(js.Opened, js.Closed);
    }

    [Fact]
    public async Task NumericFinderDoesNotCoerceAStringId()
    {
        var js = new FinderInterop();
        await using var service = new ClaySelectAdvancedSearchService(js);
        var selection = service.OpenAsync<PhoneNumberLookup, int?>("http://localhost/Default/FinderTel");
        await service.CompleteAsync(js.Opened[0], JsonSerializer.SerializeToElement("17"));
        await Assert.ThrowsAsync<JsonException>(async () => await selection);
        Assert.Equal(js.Opened, js.Closed);
    }

    private sealed class FinderInterop : IJSRuntime
    {
        public List<string> Opened { get; } = [];
        public List<string> Closed { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "claySelectAdvancedSearch.open") Opened.Add((string)args![1]!);
            if (identifier == "claySelectAdvancedSearch.cancel") Closed.Add((string)args![0]!);
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
