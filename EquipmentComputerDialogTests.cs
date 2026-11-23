using Bunit;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки компоновки и ограничений диалога конфигурации компьютера.</summary>
public sealed class EquipmentComputerDialogTests
{
    /// <summary>RAM фиксирован MemoryBanks, HD/SSD расширяются горизонтально, конфигурации имеют широкую строку.</summary>
    [Fact]
    public async Task Layout_UsesLegacyRamLimitAndHorizontalDisks()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var provider = context.Render<MudDialogProvider>();
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentComputerDialog>(
            "Компьютер", new DialogParameters<EquipmentComputerDialog>
            {
                { component => component.MemoryBanks, 2 },
                { component => component.RamDetail, "1024+2048+4096" },
                { component => component.HdDetail, "100+200" },
                { component => component.SsdDetail, "128" },
            });
        var dialog = provider.FindComponent<EquipmentComputerDialog>();

        Assert.Equal(2, dialog.Instance.Ram.Count);
        Assert.Equal(1024m, dialog.Instance.Ram[0]);
        Assert.Equal(2048m, dialog.Instance.Ram[1]);
        Assert.DoesNotContain("Добавить RAM", dialog.Markup);
        Assert.Equal(3, dialog.FindAll(".equipment-computer-components").Count);
        Assert.Equal(2, dialog.FindAll(".equipment-computer-configuration").Count);
        Assert.Contains(dialog.FindComponents<Clayzor.Lib.Web.Controls.Components.ClayButton>(),
            button => button.Instance.Text == "Добавить HD");
        Assert.Contains(dialog.FindComponents<Clayzor.Lib.Web.Controls.Components.ClayButton>(),
            button => button.Instance.Text == "Добавить SSD");
        Assert.Equal(5, dialog.FindAll(".equipment-computer-number").Count);
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
    }
}
