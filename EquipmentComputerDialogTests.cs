using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки компоновки и ограничений диалога конфигурации компьютера.</summary>
public sealed class EquipmentComputerDialogTests
{
    /// <summary>Обычные банки RAM фиксированы, лишние persisted-компоненты видны для исправления; HD/SSD горизонтальны.</summary>
    [Fact]
    public async Task Layout_PreservesExcessRamForCorrectionAndHorizontalDisks()
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

        Assert.Equal(3, dialog.Instance.Ram.Count);
        Assert.Equal(1024m, dialog.Instance.Ram[0]);
        Assert.Equal(2048m, dialog.Instance.Ram[1]);
        Assert.Equal(4096m, dialog.Instance.Ram[2]);
        Assert.DoesNotContain("Добавить RAM", dialog.Markup);
        Assert.Equal(3, dialog.FindAll(".equipment-computer-components").Count);
        Assert.Equal(2, dialog.FindAll(".equipment-computer-configuration").Count);
        Assert.Contains(dialog.FindComponents<Clayzor.Lib.Web.Controls.Components.ClayButton>(),
            button => button.Instance.Text == "Добавить HD");
        Assert.Contains(dialog.FindComponents<Clayzor.Lib.Web.Controls.Components.ClayButton>(),
            button => button.Instance.Text == "Добавить SSD");
        Assert.Equal(6, dialog.FindAll(".equipment-computer-number").Count);
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
    }

    /// <summary>Ошибка числа банков исправляется удалением лишнего RAM без потери остального черновика.</summary>
    [Fact]
    public async Task ExcessRam_CanBeCorrectedAndRetried()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentComputerSnapshot? saved = null;
        var provider = context.Render<MudDialogProvider>();
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentComputerDialog>(
            "Компьютер", new DialogParameters<EquipmentComputerDialog>
            {
                { component => component.MemoryBanks, 2 },
                { component => component.RamDetail, "1024+2048+4096" },
                { component => component.Save, snapshot =>
                {
                    saved = snapshot;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentComputerDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        Assert.Contains("превышает число банков памяти модели (2)", dialog.Markup);
        await dialog.InvokeAsync(() => dialog.FindComponents<Clayzor.Lib.Web.Controls.Components.ClayButton>()
            .Single(button => button.Instance.Text == "Удалить лишний компонент RAM").Instance.OnClick.InvokeAsync());
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.NotNull(saved);
        Assert.Equal([1024m, 2048m], saved.RamComponents);
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
    }
}
