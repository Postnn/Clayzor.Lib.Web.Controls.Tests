using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Users.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Проверяет передачу начального поискового текста из URL в форму сотрудников.
/// </summary>
public sealed class UsersSearchTests
{
    /// <summary>
    /// Параметр search отображается в поле и применяется уже к первой серверной загрузке.
    /// </summary>
    [Fact]
    public async Task InitialSearch_AppliesToFieldAndFirstSqlLoad()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Scalar = 1 },
            Script.Rows(Script.Row(("Id", 3), ("Name", "Примеров Наблюдатель"), ("NameEn", "Prim Observer"))),
        ]));
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddMudExtensions();
        var errorService = new ClayErrorService();
        ctx.Services.AddSingleton<ClayErrorService>(errorService);
        ctx.Services.AddSingleton<ISqlErrorHandler>(errorService);
        ctx.Services.AddSingleton(new ClayAppSettings());
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton(new DbManager("Server=test", errorService, () => connection));
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/search?return=1&requestId=request-1&search=Примеров");
        ctx.Services.AddSingleton<NavigationManager>(navigation);

        var page = ctx.Render<Search>();

        page.WaitForAssertion(() => Assert.Equal(2, connection.CommandLog.Count));
        var searchField = page.FindComponents<MudTextField<string>>().Single();
        Assert.Equal("Примеров", searchField.Find("input").GetAttribute("value"));
        Assert.All(connection.CommandLog, sql => Assert.Contains("Name LIKE @search", sql));
        Assert.Contains("Примеров Наблюдатель", page.Markup);
    }

    /// <summary>
    /// Страница сотрудника читает id из query string и загружает запись по параметризованному SQL.
    /// </summary>
    [Fact]
    public async Task EmployeeDetails_LoadsEmployeeByQueryId()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов Оператор"), ("Employee", "Test Operator"))),
        ]));
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddMudExtensions();
        var errorService = new ClayErrorService();
        ctx.Services.AddSingleton<ClayErrorService>(errorService);
        ctx.Services.AddSingleton<ISqlErrorHandler>(errorService);
        ctx.Services.AddSingleton(new ClayAppSettings());
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton(new DbManager("Server=test", errorService, () => connection));
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/user?id=4");
        ctx.Services.AddSingleton<NavigationManager>(navigation);

        var page = ctx.Render<EmployeeDetails>();

        page.WaitForAssertion(() => Assert.Contains("Тестов Оператор", page.Markup));
        Assert.Equal(4, page.Instance.Id);
        Assert.Contains("КодСотрудника = @Id", connection.CommandLog.Single());
        var values = page.FindComponents<MudTextField<string>>()
            .Select(field => field.Find("input").GetAttribute("value")).ToList();
        Assert.Equal(["4", "Тестов Оператор", "Test Operator"], values);
    }

    /// <summary>
    /// Новый сотрудник применяется до завершения старого запроса, а поздний ответ его не перезаписывает.
    /// </summary>
    [Fact]
    public async Task EmployeeDetails_LatePreviousResultDoesNotOverwriteNewEmployee()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                                                   System.Reflection.BindingFlags.NonPublic;
        var loadMethod = typeof(EmployeeDetails).GetMethod("LoadEmployeeAsync", flags)!;
        var loadVersionField = typeof(EmployeeDetails).GetField("_loadVersion", flags)!;
        var employeeField = typeof(EmployeeDetails).GetField("_employee", flags)!;
        var deferred = new DeferredEmployeeDetails();
        loadVersionField.SetValue(deferred, 1);

        var firstLoad = (Task)loadMethod.Invoke(deferred, [4, 1])!;
        await deferred.FirstStarted.Task;
        loadVersionField.SetValue(deferred, 2);
        var secondLoad = (Task)loadMethod.Invoke(deferred, [5, 2])!;
        await deferred.SecondStarted.Task;
        deferred.Second.SetResult(new Clayzor.Lib.Entities.Inventory.Employee
        {
            Id = 5,
            Name = "Проверочкин Рецензент",
            NameEn = "Review User",
        });
        await secondLoad;
        Assert.Equal(5, ((Clayzor.Lib.Entities.Inventory.Employee)employeeField.GetValue(deferred)!).Id);

        deferred.First.SetResult(new Clayzor.Lib.Entities.Inventory.Employee
        {
            Id = 4,
            Name = "Тестов Оператор",
            NameEn = "Test Operator",
        });
        await firstLoad;

        var current = (Clayzor.Lib.Entities.Inventory.Employee)employeeField.GetValue(deferred)!;
        Assert.Equal(5, current.Id);
        Assert.Equal("Проверочкин Рецензент", current.Name);
    }

    /// <summary>
    /// Поздний ответ прежнего запроса не скрывает индикатор загрузки нового сотрудника.
    /// </summary>
    [Fact]
    public async Task EmployeeDetails_LatePreviousResultKeepsNewLoadingIndicator()
    {
        var (ctx, navigation) = CreateDeferredEmployeeContext();
        await using var _ = ctx;
        var page = ctx.Render<DeferredEmployeeDetails>();
        var deferred = page.Instance;
        await deferred.FirstStarted.Task;

        await page.InvokeAsync(() => navigation.NavigateTo("http://localhost/user?id=5"));
        await deferred.SecondStarted.Task;
        deferred.First.SetResult(new Clayzor.Lib.Entities.Inventory.Employee
        {
            Id = 4,
            Name = "Тестов Оператор",
            NameEn = "Test Operator",
        });
        await page.InvokeAsync(() => Task.CompletedTask);
        await page.InvokeAsync(() => Task.CompletedTask);

        Assert.True(page.FindComponent<MudOverlay>().Instance.GetState(x => x.Visible));
        Assert.DoesNotContain("Тестов Оператор", page.Markup);
        deferred.Second.SetResult(new Clayzor.Lib.Entities.Inventory.Employee
        {
            Id = 5,
            Name = "Проверочкин Рецензент",
            NameEn = "Review User",
        });
        page.WaitForAssertion(() => Assert.Contains("Проверочкин Рецензент", page.Markup));
        Assert.False(page.FindComponent<MudOverlay>().Instance.GetState(x => x.Visible));
    }

    private static (BunitContext Context, FakeNavigationManager Navigation) CreateDeferredEmployeeContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddMudExtensions();
        var errorService = new ClayErrorService();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>());
        ctx.Services.AddSingleton<ClayErrorService>(errorService);
        ctx.Services.AddSingleton<ISqlErrorHandler>(errorService);
        ctx.Services.AddSingleton(new ClayAppSettings());
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton(new DbManager("Server=test", errorService, () => connection));
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/user?id=4");
        ctx.Services.AddSingleton<NavigationManager>(navigation);
        return (ctx, navigation);
    }

    private sealed class DeferredEmployeeDetails : EmployeeDetails
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Clayzor.Lib.Entities.Inventory.Employee?> First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Clayzor.Lib.Entities.Inventory.Employee?> Second { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<Clayzor.Lib.Entities.Inventory.Employee?> GetEmployeeAsync(int id)
        {
            if (id == 4)
            {
                FirstStarted.TrySetResult();
                return First.Task;
            }
            SecondStarted.TrySetResult();
            return Second.Task;
        }
    }
}
