using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Dialogs;
using Kesco.App.Web.Inventory.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Регрессионные тесты интеграции ClaySelect на демонстрационной странице Inventory.
/// </summary>
public sealed class ClaySelectInventoryDemoTests
{
    /// <summary>
    /// Смена ключа уничтожает прежний компонент, и поздний ответ старого источника не попадает в новый список.
    /// </summary>
    [Fact]
    public async Task KeyChange_PreventsLateResultFromPreviousSelect()
    {
        using var ctx = CreateContext(CreateDb(new Queue<ScriptEntry>()));
        var key = 1;
        var oldSource = new DeferredEmployeeSource();
        IClaySelectDataSource<Employee, int?> currentSource = oldSource;
        RenderFragment content = builder =>
        {
            builder.OpenComponent<ClaySelect<Employee, int?>>(0);
            builder.SetKey(key);
            builder.AddAttribute(1, "DataSource", currentSource);
            builder.AddAttribute(2, "ValueSelector", (Func<Employee, int?>)(item => item.Id));
            builder.AddAttribute(3, "TextSelector", (Func<Employee, string>)(item => item.Name));
            builder.AddAttribute(4, "Options", new ClaySelectOptions { DebounceInterval = 0, AutoSelectExactMatch = false });
            builder.CloseComponent();
        };
        var host = ctx.Render<MudForm>(parameters => parameters.Add(component => component.ChildContent, content));
        var oldSelect = host.FindComponent<ClaySelect<Employee, int?>>();
        oldSelect.Find(".clay-select-button").Click();
        oldSelect.Find("input").Input("old");

        key = 2;
        currentSource = new DeferredEmployeeSource();
        host.Render(parameters => parameters.Add(component => component.ChildContent, content));
        await host.InvokeAsync(() => oldSource.Complete("old", new Employee { Id = 1, Name = "Старый результат" }));
        await host.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("Старый результат", host.Markup);
    }

    /// <summary>
    /// Повторный вход на страницу создаёт новый источник моделей с пустым фильтром.
    /// </summary>
    [Fact]
    public void PageReentry_CreatesCleanModelSource()
    {
        using var ctx = CreateContext(CreateDb(new Queue<ScriptEntry>()));
        var firstPage = ctx.Render<SelectTest>();
        var firstSource = firstPage.FindComponent<ClaySelect<EquipmentModel, int?>>()
            .Instance.DataSource as EquipmentModelSelectDataSource;
        Assert.NotNull(firstSource);
        firstSource.Filter.EquipmentTypeId = 42;
        firstPage.Dispose();

        var secondPage = ctx.Render<SelectTest>();
        var secondSource = secondPage.FindComponent<ClaySelect<EquipmentModel, int?>>()
            .Instance.DataSource as EquipmentModelSelectDataSource;

        Assert.NotNull(secondSource);
        Assert.NotSame(firstSource, secondSource);
        Assert.Null(secondSource.Filter.EquipmentTypeId);
    }

    /// <summary>
    /// Начальная загрузка диалога отображает сотрудников без дополнительного клика.
    /// </summary>
    [Fact]
    public async Task EmployeeDialog_InitialLoadRendersResults()
    {
        DapperColumnMapper.Initialize();
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодСотрудника", 1),
                ("Сотрудник", "Иван Иванов"),
                ("Employee", "Ivan Ivanov"))),
        ]);
        var ctx = CreateContext(CreateDb(entries));
        var provider = ctx.Render<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();

        await provider.InvokeAsync(async () =>
        {
            var parameters = new DialogParameters<EmployeeSearchDialog>
            {
                { dialog => dialog.InitialSearch, "Иван" },
            };
            await service.ShowAsync<EmployeeSearchDialog>("Поиск сотрудника", parameters);
        });

        provider.WaitForAssertion(() => Assert.Contains("Иван Иванов", provider.Markup));
        await ctx.Services.DisposeAsync();
        ctx.Dispose();
    }

    private static TestContext CreateContext(DbManager db)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddMudExtensions();
        ctx.Services.AddSingleton(db);
        ctx.Services.AddSingleton(new EquipmentTypeSelectDataSource(db));
        ctx.Services.AddSingleton(new EmployeeSelectDataSource(db));
        return ctx;
    }

    private static DbManager CreateDb(Queue<ScriptEntry> entries)
    {
        var connection = new ScriptedConnection(entries);
        return new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
    }

    private sealed class DeferredEmployeeSource : IClaySelectDataSource<Employee, int?>
    {
        private readonly Dictionary<string, TaskCompletionSource<ClaySelectResult<Employee>>> _searches = new();

        public Task<ClaySelectResult<Employee>> SearchAsync(
            ClaySelectSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.SearchText.Length == 0)
                return Task.FromResult(new ClaySelectResult<Employee>());
            var completion = new TaskCompletionSource<ClaySelectResult<Employee>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _searches[request.SearchText] = completion;
            return completion.Task;
        }

        public Task<Employee?> GetByIdAsync(int? id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Employee?>(null);

        public void Complete(string text, Employee employee) =>
            _searches[text].TrySetResult(new ClaySelectResult<Employee> { Items = [employee] });
    }
}
