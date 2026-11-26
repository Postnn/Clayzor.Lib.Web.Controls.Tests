using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
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
            builder.AddAttribute(4, "Options", new ClaySelectOptions { AutoSelectExactMatch = false });
            builder.CloseComponent();
        };
        var host = ctx.Render<MudForm>(parameters => parameters.Add(component => component.ChildContent, content));
        var oldSelect = host.FindComponent<ClaySelect<Employee, int?>>();
        oldSelect.Find(".clay-select-button").Click();
        oldSelect.Find("input").Input("old");
        oldSelect.Find("input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

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
    /// Уход со страницы отменяет браузерный запрос, а поздний ответ не возвращает фокус в уничтоженный компонент.
    /// </summary>
    [Fact]
    public async Task PageDisposal_CancelsExternalSearchAndIgnoresLateResult()
    {
        using var ctx = CreateContext(CreateDb(new Queue<ScriptEntry>([Script.Rows()])));
        var page = ctx.Render<SelectTest>();
        var select = page.FindComponent<ClaySelect<Employee, int?>>();
        select.Find(".clay-select-button").Click();
        select.Find(".clay-select-advanced").Click();
        var open = ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelectAdvancedSearch.open");
        var requestId = (string)open.Arguments[1]!;
        var service = ctx.Services.GetRequiredService<Clayzor.Lib.Web.Controls.Services.ClaySelectAdvancedSearchService>();
        var focusCount = ctx.JSInterop.Invocations.Count(item => item.Identifier == "focus");

        page.Instance.Dispose();
        page.Dispose();
        var cancelled = false;
        for (var attempt = 0; attempt < 20 && !cancelled; attempt++)
        {
            cancelled = await page.InvokeAsync(() => ctx.JSInterop.Invocations.Any(item =>
                item.Identifier == "claySelectAdvancedSearch.cancel" && Equals(item.Arguments[0], requestId)));
            if (!cancelled)
                await Task.Delay(50);
        }
        Assert.True(cancelled);
        using var document = System.Text.Json.JsonDocument.Parse("3");
        Assert.Equal("Stale", await service.ApplyAsync(requestId, 1, document.RootElement.Clone()));

        Assert.Equal(focusCount, ctx.JSInterop.Invocations.Count(item => item.Identifier == "focus"));
    }

    /// <summary>Внешний поиск подтверждается после восстановления и применения сотрудника на форме.</summary>
    [Fact]
    public async Task DemoAcknowledgesOnlyAfterEmployeeIsApplied()
    {
        Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();
        using var ctx = CreateContext(CreateDb(new Queue<ScriptEntry>([
            Script.Rows(),
            Script.Rows(),
            Script.Rows(Script.Row(("КодСотрудника", 42), ("Сотрудник", "Сотрудник из поиска")))
        ])));
        var page = ctx.Render<SelectTest>();
        var select = page.FindComponent<ClaySelect<Employee, int?>>();
        select.Find(".clay-select-button").Click();
        select.Find(".clay-select-advanced").Click();
        var open = ctx.JSInterop.Invocations.Single(item => item.Identifier == "claySelectAdvancedSearch.open");
        Assert.Equal(true, open.Arguments[4]);
        var requestId = (string)open.Arguments[1]!;
        var service = ctx.Services.GetRequiredService<Clayzor.Lib.Web.Controls.Services.ClaySelectAdvancedSearchService>();

        try
        {
            Assert.Equal("Rejected", await service.ApplyAsync(requestId, 1, System.Text.Json.JsonSerializer.SerializeToElement(11)));
            Assert.Null(select.Instance.Value);
            Assert.Equal("Accepted", await service.ApplyAsync(requestId, 2, System.Text.Json.JsonSerializer.SerializeToElement(42)));
            Assert.Equal("Accepted", await service.ApplyAsync(requestId, 2, System.Text.Json.JsonSerializer.SerializeToElement(42)));
            Assert.Equal(42, select.Instance.Value);
            Assert.Equal("Сотрудник из поиска", select.Find("input").GetAttribute("value"));
        }
        finally { await service.CancelAsync(requestId); }
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
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager
        {
            ["URI_user_search"] = "http://localhost:5030/search",
            ["URI_user_form"] = "http://localhost:5030/user",
        };
        ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        ctx.Services.AddSingleton(new Clayzor.Lib.Web.Controls.Services.ClaySelectAdvancedSearchService(ctx.JSInterop.JSRuntime));
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
