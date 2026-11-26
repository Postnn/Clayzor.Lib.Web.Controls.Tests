using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверка обязательности выбора устройства в диалоге подключения UPS.</summary>
public sealed class EquipmentUpsLinkRequiredTests
{
    /// <summary>Пустой диалог объявляет UPS обязательным и блокирует подключение.</summary>
    [Fact]
    public async Task EmptyUpsSelectIsRequired()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddKeyedSingleton("ups", new UpsEquipmentSelectDataSource(
            new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => new ScriptedConnection(new Queue<ScriptEntry>())), true));
        context.Render<MudPopoverProvider>();
        var provider = context.Render<MudDialogProvider>();

        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentUpsLinkDialog>("Подключение UPS");
        var dialog = provider.FindComponent<EquipmentUpsLinkDialog>();
        var input = dialog.Find("input[role=combobox][required][data-clay-required-empty]");
        Assert.Equal("Выберите UPS", input.GetAttribute("placeholder"));
        Assert.True(dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Подключить").HasAttribute("disabled"));
    }
}
