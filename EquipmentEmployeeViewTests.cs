using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки перехода из выбора сотрудника в его карточку.</summary>
public sealed class EquipmentEmployeeViewTests
{
    /// <summary>Выбор ответственного открывает карточку выбранного сотрудника.</summary>
    [Fact]
    public Task ResponsibleDialog_OpensSelectedEmployeeCard() =>
        AssertEmployeeViewOpensUserForm<EquipmentResponsibleDialog>(parameters =>
        {
            parameters.Add(component => component.Mode, EquipmentResponsibleDialogMode.Issue);
            parameters.Add(component => component.EmployeeId, 4);
        });

    /// <summary>Выбор сотрудника при перемещении «На руках» открывает его карточку.</summary>
    [Fact]
    public Task LocationDialog_OpensSelectedEmployeeCard() =>
        AssertEmployeeViewOpensUserForm<EquipmentLocationDialog>(parameters =>
        {
            parameters.Add(component => component.RequireResponsible, true);
            parameters.Add(component => component.LocationId, 55);
            parameters.Add(component => component.EmployeeId, 4);
        });

    private static async Task AssertEmployeeViewOpensUserForm<TComponent>(
        Action<DialogParameters<TComponent>> configure)
        where TComponent : ComponentBase
    {
        DapperColumnMapper.Initialize();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost/Users/UserForm",
        });
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb(Script.Rows(EmployeeRow()))));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb(Script.Rows(LocationRow()))));
        context.Services.AddSingleton(new Kesco.App.Web.Inventory.Services.EquipmentFormService(
            CreateDb(Script.Rows()), new TestAuthenticationStateProvider()));
        var provider = context.Render<MudDialogProvider>();
        var parameters = new DialogParameters<TComponent>();
        configure(parameters);
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<TComponent>("Тест", parameters);
        var page = provider.FindComponent<TComponent>();
        var view = page.WaitForElement("button[aria-label='Просмотреть сущность']");

        view.Click();

        var call = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "clayOpen");
        Assert.Equal("http://localhost/Users/UserForm?id=4", call.Arguments[0]);
        Assert.Equal("_blank", call.Arguments[1]);
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));

    private static Dictionary<string, object?> EmployeeRow() => Script.Row(
        ("КодСотрудника", 4), ("Сотрудник", "Тестов А."), ("Employee", "Test A."));

    private static Dictionary<string, object?> LocationRow() => Script.Row(
        ("КодРасположения", 1), ("РасположениеPath1", "CLAY_TEST / Room"));
}
