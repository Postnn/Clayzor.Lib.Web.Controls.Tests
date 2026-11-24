using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки календарной даты и движущегося now в редакторах EQ9-11.</summary>
public sealed class Eq9CalendarDialogTests
{
    /// <summary>SIM UI использует business Today и после исправления даты сохраняет тот же черновик.</summary>
    [Fact]
    public async Task SimServiceApply_UsesBusinessTodayInsteadOfServerToday()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var businessToday = new DateTime(2099, 2, 28);
        var value = new EquipmentSimService
        {
            EquipmentId = 7,
            PhoneNumber = "79000000001",
            ServiceTypeId = "_TEST",
            ServiceName = "Test",
            DateFrom = businessToday.AddDays(1),
        };
        EquipmentSimService? saved = null;
        var provider = context.Render<MudDialogProvider>();
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentSimServiceDialog>(
            "SIM service", new DialogParameters<EquipmentSimServiceDialog>
            {
                { dialog => dialog.Value, value },
                { dialog => dialog.Today, businessToday },
                { dialog => dialog.Save, draft =>
                {
                    saved = draft;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentSimServiceDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        Assert.Contains("Фактические даты не могут быть в будущем.", dialog.Markup);
        Assert.Null(saved);

        dialog.Instance.Value.DateFrom = businessToday.AddHours(12);
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.Same(value, saved);
    }

    /// <summary>Открытый пять минут Responsible Editor получает новое now и принимает ввод, ставший текущим.</summary>
    [Fact]
    public async Task ResponsibleApply_AfterFiveMinutes_UsesCurrentClientNow()
    {
        await using var context = CreateResponsibleContext();
        var openedAt = new DateTime(2099, 2, 28, 10, 0, 0);
        var appliedAt = openedAt.AddMinutes(5);
        var getNowCalls = 0;
        EquipmentResponsibleDialogResult? saved = null;
        var provider = context.Render<MudDialogProvider>();
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentResponsibleDialog>(
            "Responsible", new DialogParameters<EquipmentResponsibleDialog>
            {
                { dialog => dialog.Mode, EquipmentResponsibleDialogMode.Issue },
                { dialog => dialog.Now, openedAt },
                { dialog => dialog.GetNow, () =>
                {
                    getNowCalls++;
                    return Task.FromResult(appliedAt);
                } },
                { dialog => dialog.EmployeeId, 4 },
                { dialog => dialog.FromDate, openedAt.Date },
                { dialog => dialog.FromTime, openedAt.AddMinutes(3).TimeOfDay },
                { dialog => dialog.ReturnDate, openedAt.Date },
                { dialog => dialog.Save, draft =>
                {
                    saved = draft;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentResponsibleDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.Equal(1, getNowCalls);
        Assert.NotNull(saved);
        Assert.Equal(openedAt.AddMinutes(3), saved.FromLocal);
        Assert.Equal(openedAt.Date, saved.ReturnDate);
    }

    private static BunitContext CreateResponsibleContext()
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb(Script.Rows(
            Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."), ("Employee", "Test A."))))));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb()));
        return context;
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));
}
