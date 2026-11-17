using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Профильные тесты формы модели оборудования.</summary>
public sealed class ModelEquipmentFormTests
{
    /// <summary>Начальный тип показывает зависимые поля и задаёт Net равным нулю.</summary>
    [Fact]
    public async Task NewComputerModel_AppliesInitialTypeAndDefaults()
    {
        var fixture = CreateFixture("http://localhost/ModelEquipmentForm?codetype=5&_codetype=5", [
            Script.Rows(TypeRow(5, computer: 1, mac: 2)), Script.Rows(),
        ]);
        await using var _ = fixture.Context;

        var page = fixture.Context.Render<ModelEquipmentForm>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("CPU:", page.Markup);
            Assert.Contains("Net:", page.Markup);
            Assert.DoesNotContain("Размер монитора:", page.Markup);
        }, TimeSpan.FromSeconds(3));
        var model = CurrentModel(page);
        Assert.Equal(5, model.EquipmentTypeId);
        Assert.Equal(0, model.NetworkInterfaces);
        Assert.Equal(0, model.MemoryTypeId);
        Assert.Equal(0, model.MemoryBanks);
    }

    /// <summary>При смене типа скрытые характеристики очищаются.</summary>
    [Fact]
    public async Task TypeChange_ResetsDependentValues()
    {
        var fixture = CreateFixture("http://localhost/ModelEquipmentForm?codetype=5", [
            Script.Rows(TypeRow(5, computer: 1, mac: 2)), Script.Rows(),
        ]);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("CPU:", page.Markup), TimeSpan.FromSeconds(3));
        var model = CurrentModel(page);
        model.Name = "Test";
        model.Cpu = "Intel";
        model.MemoryTypeId = 3;
        model.MemoryBanks = 4;
        model.NetworkInterfaces = 2;
        model.NetworkName = "LAN";
        var select = page.FindComponent<ClaySelect<EquipmentType, int?>>();

        await page.InvokeAsync(() => select.Instance.SelectedItemChanged.InvokeAsync(TypeRowEntity(7, monitor: 1)));

        page.WaitForAssertion(() =>
        {
            Assert.Equal(string.Empty, model.Cpu);
            Assert.Equal(0, model.MemoryTypeId);
            Assert.Equal(0, model.MemoryBanks);
            Assert.Equal(0, model.NetworkInterfaces);
            Assert.Equal(string.Empty, model.NetworkName);
            Assert.Contains("Размер монитора:", page.Markup);
            Assert.DoesNotContain("CPU:", page.Markup);
        });
    }

    /// <summary>Дубликат модели и PN блокирует сохранение текущей записи.</summary>
    [Fact]
    public async Task DuplicateModel_DoesNotSave()
    {
        var fixture = CreateFixture("http://localhost/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
        ], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Old", PartNumber = "PN" });
        fixture.Service.Duplicate = true;
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Old", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Duplicate");

        FindButton(page, "Применить").Click();

        page.WaitForAssertion(() => Assert.Contains("уже существует", page.Markup));
        Assert.Equal(0, fixture.Service.SaveCalls);
        Assert.Equal(7, fixture.Service.LastDuplicateModel?.Id);
    }

    /// <summary>Применение новой модели сохраняет её и добавляет id в URL.</summary>
    [Fact]
    public async Task ApplyNewModel_SavesAndNavigatesToCreatedId()
    {
        var fixture = CreateFixture("http://localhost/ModelEquipmentForm?codetype=5", [
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
        ]);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("CPU:", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Created model");

        FindButton(page, "Применить").Click();

        page.WaitForAssertion(() => Assert.EndsWith("/ModelEquipmentForm?id=42", fixture.Navigation.Uri), TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Service.SaveCalls);
        Assert.Equal("Created model", fixture.Service.SavedModel?.Name);
    }

    /// <summary>OK на неизменённой записи закрывает форму без сохранения.</summary>
    [Fact]
    public async Task OkWithoutChanges_ClosesWithoutSave()
    {
        var fixture = CreateFixture("http://localhost/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5)), Script.Rows(),
        ], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing", PartNumber = "PN" });
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup), TimeSpan.FromSeconds(3));

        FindButton(page, "OK").Click();

        page.WaitForAssertion(() => Assert.Contains(fixture.Context.JSInterop.Invocations,
            call => call.Identifier == "clayModelEquipmentForm.close"));
        Assert.Equal(0, fixture.Service.SaveCalls);
    }

    /// <summary>Поздний ответ прежнего id не перезаписывает новую модель.</summary>
    [Fact]
    public async Task LatePreviousLoad_DoesNotOverwriteNewModel()
    {
        var fixture = CreateFixture("http://localhost/ModelEquipmentForm?id=4", [Script.Rows()]);
        fixture.Service.DeferredModels = true;
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        fixture.Service.WaitForRequest(4);
        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/ModelEquipmentForm?id=5"));
        fixture.Service.WaitForRequest(5);
        fixture.Service.Complete(5, new EquipmentModel { Id = 5, EquipmentTypeId = 0, Name = "New" });
        page.WaitForAssertion(() => Assert.Contains("New", page.Markup), TimeSpan.FromSeconds(3));

        fixture.Service.Complete(4, new EquipmentModel { Id = 4, EquipmentTypeId = 0, Name = "Old" });

        page.WaitForAssertion(() =>
        {
            Assert.Equal(5, CurrentModel(page).Id);
            Assert.Contains("New", page.Markup);
            Assert.DoesNotContain("Old", page.Markup);
        });
    }

    private static Fixture CreateFixture(string uri, IEnumerable<ScriptEntry> entries, EquipmentModel? model = null)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries));
        var db = CreateDb(connection);
        var service = new FakeModelEquipmentFormService(db) { Model = model };
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo(uri);
        var configuration = new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost:5030/user",
            ["URI_resource_form"] = "http://localhost/resource",
            ["URI_resource_search"] = "http://localhost/resource-search",
        };
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton<ModelEquipmentFormService>(service);
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(db));
        context.Services.AddSingleton(new MemoryTypeSelectDataSource(db));
        context.Services.AddSingleton(new ResourceSelectDataSource(CreateDb(new ScriptedConnection(new Queue<ScriptEntry>()))));
        context.Services.AddSingleton(new ClaySelectAdvancedSearchService(context.JSInterop.JSRuntime));
        return new Fixture(context, service, navigation);
    }

    private static DbManager CreateDb(ScriptedConnection connection) => new("Server=test", new FakeSqlErrorHandler(), () => connection);

    private static EquipmentModel CurrentModel(IRenderedComponent<ModelEquipmentForm> page) =>
        (EquipmentModel)typeof(ModelEquipmentForm)
            .GetField("_model", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page.Instance)!;

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<ModelEquipmentForm> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private static Dictionary<string, object?> TypeRow(int id, int computer = 0, int monitor = 0, int mac = 0) => Script.Row(
        ("КодТипаОборудования", id), ("ТипОборудования", $"Тип {id}"),
        ("ЕстьХарактеристикиКомпьютера", (byte)computer),
        ("ЕстьХарактеристикиМонитора", (byte)monitor), ("ЕстьMACадрес", (byte)mac));

    private static EquipmentType TypeRowEntity(int id, int computer = 0, int monitor = 0, int mac = 0) => new()
    {
        Id = id, Name = $"Тип {id}", HasComputerCharacteristics = (byte)computer,
        HasMonitorCharacteristics = (byte)monitor, MacAddressCount = (byte)mac,
    };

    private sealed record Fixture(BunitContext Context, FakeModelEquipmentFormService Service, FakeNavigationManager Navigation);

    private sealed class FakeModelEquipmentFormService : ModelEquipmentFormService
    {
        private readonly Dictionary<int, TaskCompletionSource<EquipmentModel?>> _deferred = new();

        public FakeModelEquipmentFormService(DbManager db) : base(db) { }
        public EquipmentModel? Model { get; set; }
        public EquipmentModel? SavedModel { get; private set; }
        public EquipmentModel? LastDuplicateModel { get; private set; }
        public bool Duplicate { get; set; }
        public bool DeferredModels { get; set; }
        public int SaveCalls { get; private set; }

        public override Task<EquipmentModel?> GetModelAsync(int id)
        {
            if (!DeferredModels)
                return Task.FromResult(id == 0 ? null : Model?.Id == id ? Model : SavedModel?.Id == id ? SavedModel : null);
            var completion = new TaskCompletionSource<EquipmentModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deferred[id] = completion;
            return completion.Task;
        }

        public override Task<bool> HasDuplicateAsync(EquipmentModel model)
        {
            LastDuplicateModel = model;
            return Task.FromResult(Duplicate);
        }

        public override Task<EquipmentModelUsage> GetUsageAsync(int id, int? newEquipmentTypeId = null) =>
            Task.FromResult(new EquipmentModelUsage());

        public override Task<int> SaveAsync(EquipmentModel model)
        {
            SaveCalls++;
            if (model.Id == 0)
                model.Id = 42;
            SavedModel = model;
            return Task.FromResult(model.Id);
        }

        public override Task<bool> HasRoleAsync(int roleId) => Task.FromResult(true);

        public void WaitForRequest(int id)
        {
            for (var attempt = 0; attempt < 100 && !_deferred.ContainsKey(id); attempt++)
                Thread.Sleep(10);
            Assert.True(_deferred.ContainsKey(id), $"Запрос модели {id} не был начат");
        }

        public void Complete(int id, EquipmentModel model) => _deferred[id].TrySetResult(model);
    }
}
