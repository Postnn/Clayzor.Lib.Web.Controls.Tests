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
        context.Services.AddScoped<Clayzor.Lib.Web.Controls.Services.ClayDraftService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EquipmentFormService(CreateDb(), new TestAuthenticationStateProvider()));
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

    /// <summary>Ошибка чтения SIM-кодов блокирует OK и не приводит к SQL UPDATE.</summary>
    [Fact]
    public async Task SecretsReadFailure_DisablesApplyAndDoesNotWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(53, "connection error")),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        var reference = await ShowExistingAsync(context);

        var dialog = provider.FindComponent<EquipmentSimDialog>();
        dialog.WaitForAssertion(() => Assert.Contains("сохранение заблокировано", dialog.Markup));
        Assert.True(dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        Assert.False(reference.Result.IsCompleted);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));

        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").Click();
        Assert.True((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2))).Canceled);
    }

    /// <summary>Изменение договора существующей SIM сохраняет ранее прочитанные коды.</summary>
    [Fact]
    public async Task ContractEdit_PreservesLoadedSecrets()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("PIN", "1234"), ("PIN2", "12345"),
                ("PUK", "12345678"), ("PUK2", "87654321"))),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        var reference = await ShowExistingAsync(context);
        var dialog = provider.FindComponent<EquipmentSimDialog>();
        dialog.WaitForAssertion(() => Assert.Equal("1234", dialog.Instance.Secrets.Pin));

        dialog.Instance.ContractId = 11;
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").Click();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        var value = Assert.IsType<EquipmentSimDialogResult>(result.Data);
        Assert.Equal(11, value.ContractId);
        Assert.Equal("1234", value.Secrets.Pin);
        Assert.Equal("87654321", value.Secrets.Puk2);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
    }

    /// <summary>Сохранённое несоответствующее лицо получает advisory и не удаляется.</summary>
    [Fact]
    public async Task ExistingInvalidPerson_ShowsAdvisoryWithoutClearingSelection()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(), Script.Scalar(0),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        await ShowExistingAsync(context, 9);
        var dialog = provider.FindComponent<EquipmentSimDialog>();

        dialog.WaitForAssertion(() => Assert.Contains("не соответствует договору", dialog.Markup));
        Assert.Equal(9, dialog.Instance.PersonalDataPersonId);
    }

    /// <summary>Ошибка advisory отличается от отрицательного результата и сохраняет лицо.</summary>
    [Fact]
    public async Task AdvisoryReadFailure_IsDistinctAndPreservesSelection()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(), Script.Error(SqlExceptionFactory.Create(50000, "read failed")),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        await ShowExistingAsync(context, 9);
        var dialog = provider.FindComponent<EquipmentSimDialog>();

        dialog.WaitForAssertion(() => Assert.Contains("не отрицательный результат", dialog.Markup));
        Assert.Equal(9, dialog.Instance.PersonalDataPersonId);
    }

    private static BunitContext CreateContext(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddScoped<Clayzor.Lib.Web.Controls.Services.ClayDraftService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider()));
        context.Services.AddSingleton(new MobileContractSelectDataSource(CreateDb(
            Script.Rows(Script.Row(("КодДокумента", 10), ("ДоговорНазвание", "CLAY_TEST"), ("ТипЛицаЗаказчика", 0))))));
        context.Services.AddSingleton(new PersonalDataPersonSelectDataSource(CreateDb()));
        return context;
    }

    private static async Task<IDialogReference> ShowExistingAsync(BunitContext context, int? personalDataPersonId = null) =>
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentSimDialog>(
            "SIM", new DialogParameters<EquipmentSimDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.HasExisting, true },
                { dialog => dialog.PhoneNumber, "79000000001" },
                { dialog => dialog.ActivatedDate, new DateTime(2026, 11, 18) },
                { dialog => dialog.ActivatedTime, new TimeSpan(8, 30, 0) },
                { dialog => dialog.ContractId, 10 },
                { dialog => dialog.PersonalDataPersonId, personalDataPersonId },
            });

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
