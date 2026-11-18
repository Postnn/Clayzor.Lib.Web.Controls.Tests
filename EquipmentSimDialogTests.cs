using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки повторной валидации диалога SIM.</summary>
public sealed class EquipmentSimDialogTests
{
    /// <summary>После ошибки обязательной даты следующее нажатие выполняет валидацию заново и закрывает диалог.</summary>
    [Fact]
    public async Task ApplyAfterValidationError_RevalidatesAndReturnsValue()
    {
        DapperColumnMapper.Initialize();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EquipmentFormService(CreateDb()));
        context.Services.AddSingleton(new MobileContractSelectDataSource(CreateDb(
            Script.Rows(Script.Row(("КодДокумента", 10), ("ДоговорНазвание", "CLAY_TEST"), ("ТипЛицаЗаказчика", 0))))));
        context.Services.AddSingleton(new PersonalDataPersonSelectDataSource(CreateDb()));
        var provider = context.Render<MudDialogProvider>();
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentSimDialog>(
            "SIM", new DialogParameters<EquipmentSimDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.PhoneNumber, "79000000001" },
                { dialog => dialog.ContractId, 10 },
            });
        var dialog = provider.FindComponent<EquipmentSimDialog>();
        var ok = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK");
        ok.Click();
        Assert.Contains("Укажите дату и время активации", dialog.Markup);

        dialog.Instance.ActivatedDate = new DateTime(2026, 11, 18);
        dialog.Instance.ActivatedTime = new TimeSpan(8, 30, 0);
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").Click();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.False(result.Canceled);
        var value = Assert.IsType<EquipmentSimDialogResult>(result.Data);
        Assert.Equal(new DateTime(2026, 11, 18, 8, 30, 0), value.ActivatedLocal);
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));
}
