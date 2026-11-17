using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5&_codetype=5", [
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

    /// <summary>При подтверждённой смене типа скрытые характеристики очищаются.</summary>
    [Fact]
    public async Task TypeChange_ResetsDependentValues()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5", [
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

    /// <summary>Отмена редактирования текста типа не изменяет характеристики модели.</summary>
    [Fact]
    public async Task TypeTextEditAndEscape_PreservesAllCharacteristics()
    {
        var original = new EquipmentModel
        {
            Id = 7, EquipmentTypeId = 5, Name = "Computer", PartNumber = "PN", Cpu = "Intel",
            MonitorSize = 12.5, IsMultimedia = true, MemoryTypeId = 3, MemoryBanks = 4,
            NetworkInterfaces = 1, NetworkName = "LAN", PhoneTemplate = "template",
        };
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5, computer: 1, mac: 2)), Script.Rows(),
        ], original);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Intel", page.Markup), TimeSpan.FromSeconds(3));
        var select = page.FindComponent<ClaySelect<EquipmentType, int?>>();

        select.Find("input").Input("Другой тип");
        select.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        page.WaitForAssertion(() =>
        {
            var model = CurrentModel(page);
            Assert.Equal(5, model.EquipmentTypeId);
            Assert.Equal("Intel", model.Cpu);
            Assert.Equal(12.5, model.MonitorSize);
            Assert.True(model.IsMultimedia);
            Assert.Equal(3, model.MemoryTypeId);
            Assert.Equal(4, model.MemoryBanks);
            Assert.Equal(1, model.NetworkInterfaces);
            Assert.Equal("LAN", model.NetworkName);
            Assert.Equal("template", model.PhoneTemplate);
        });
    }

    /// <summary>Дубликат модели и PN блокирует сохранение текущей записи.</summary>
    [Fact]
    public async Task DuplicateModel_DoesNotSave()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
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

    /// <summary>Применение новой модели загружает сохранённую запись, аудит и чистый snapshot.</summary>
    [Fact]
    public async Task ApplyNewModel_SavesNavigatesAndReloadsCreatedModel()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5", [
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
        ]);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("CPU:", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Created model");

        FindButton(page, "Применить").Click();

        page.WaitForAssertion(() =>
        {
            Assert.EndsWith("/ModelEquipmentForm?id=42", fixture.Navigation.Uri);
            Assert.Equal(42, CurrentModel(page).Id);
            Assert.Equal(4, CurrentModel(page).ChangedById);
            Assert.NotNull(CurrentModel(page).ChangedAt);
            Assert.False(IsModified(page.Instance));
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Service.SaveCalls);
        Assert.Equal("Created model", fixture.Service.SavedModel?.Name);
    }

    /// <summary>SQL-ошибка сохраняет введённые данные и не запускает закрытие или success-path.</summary>
    [Fact]
    public async Task SaveSqlException_KeepsPageAndInputWithoutSuccessPath()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5)), Script.Rows(),
        ], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing", PartNumber = "PN" });
        fixture.Service.SaveException = SqlExceptionFactory.Create(50000, "trigger");
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Changed");

        await page.InvokeAsync(() => InvokeSave(page.Instance, true));

        Assert.Equal("Changed", CurrentModel(page).Name);
        Assert.False(IsSaving(page.Instance));
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier is "clayModelEquipmentForm.close" or "clayModelEquipmentForm.refreshOpener");
        Assert.Contains("не сохранена", page.Markup);
    }

    /// <summary>Нулевой ID после INSERT не вызывает навигацию и обновление родителя.</summary>
    [Fact]
    public async Task InsertWithoutId_DoesNotNavigateOrRefreshOpener()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5", [
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
        ]);
        fixture.Service.SaveResult = 0;
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("CPU:", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Created model");
        var originalUri = fixture.Navigation.Uri;

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal(originalUri, fixture.Navigation.Uri);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.Contains("не вернула идентификатор", page.Markup);
    }

    /// <summary>Disposal во время проверки дубликата отменяет последующий success-path.</summary>
    [Fact]
    public async Task DisposeDuringSave_DoesNotSaveOrInvokeJsAfterLateContinuation()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5)), Script.Rows(),
        ], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing", PartNumber = "PN" });
        fixture.Service.DeferredDuplicate = true;
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Changed");
        var saveTask = page.InvokeAsync(() => InvokeSave(page.Instance, true));
        fixture.Service.WaitForDuplicate();

        page.Instance.Dispose();
        page.Dispose();
        fixture.Service.CompleteDuplicate(false);
        await saveTask;

        Assert.Equal(0, fixture.Service.SaveCalls);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier is "clayModelEquipmentForm.close" or "clayModelEquipmentForm.refreshOpener");
    }

    /// <summary>OK на неизменённой записи закрывает форму без сохранения.</summary>
    [Fact]
    public async Task OkWithoutChanges_ClosesWithoutSave()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
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

    /// <summary>Поздний ответ прежнего id после полного завершения операции не перезаписывает новую модель.</summary>
    [Fact]
    public async Task LatePreviousLoad_AfterBothLoadsComplete_DoesNotOverwriteNewModel()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var db = CreateDb(connection);
        var service = new FakeModelEquipmentFormService(db) { DeferredModels = true };
        var page = new ModelEquipmentForm();
        SetInjected(page, "Service", service);
        SetInjected(page, "EquipmentTypeSource", new EquipmentTypeSelectDataSource(db));
        SetInjected(page, "MemoryTypeSource", new MemoryTypeSelectDataSource(db));
        var loadVersion = PrivateMember("_loadVersion", page);
        var load = PrivateMethod("LoadDataAsync");
        loadVersion.SetValue(page, 1);
        var firstLoad = (Task)load.Invoke(page, [4, 0, 1])!;
        service.WaitForRequest(4);
        loadVersion.SetValue(page, 2);
        var secondLoad = (Task)load.Invoke(page, [5, 0, 2])!;
        service.WaitForRequest(5);
        service.Complete(5, new EquipmentModel { Id = 5, EquipmentTypeId = 0, Name = "New" });
        await secondLoad;

        service.Complete(4, new EquipmentModel { Id = 4, EquipmentTypeId = 0, Name = "Old" });
        await firstLoad;

        Assert.Equal(5, CurrentModel(page).Id);
        Assert.Equal("New", CurrentModel(page).Name);
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

    private static EquipmentModel CurrentModel(IRenderedComponent<ModelEquipmentForm> page) => CurrentModel(page.Instance);

    private static EquipmentModel CurrentModel(ModelEquipmentForm page) =>
        (EquipmentModel)PrivateMember("_model", page).GetValue(page)!;

    private static bool IsModified(ModelEquipmentForm page) =>
        (bool)typeof(ModelEquipmentForm).GetProperty("IsModified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static bool IsSaving(ModelEquipmentForm page) => (bool)PrivateMember("_saving", page).GetValue(page)!;

    private static Task InvokeSave(ModelEquipmentForm page, bool close) =>
        (Task)PrivateMethod("SaveAsync").Invoke(page, [close])!;

    private static MethodInfo PrivateMethod(string name) =>
        typeof(ModelEquipmentForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo PrivateMember(string name, ModelEquipmentForm page) =>
        typeof(ModelEquipmentForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void SetInjected(ModelEquipmentForm page, string name, object value) =>
        typeof(ModelEquipmentForm).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(page, value);

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
        private TaskCompletionSource<bool>? _duplicateCompletion;

        public FakeModelEquipmentFormService(DbManager db) : base(db) { }
        public EquipmentModel? Model { get; set; }
        public EquipmentModel? SavedModel { get; private set; }
        public EquipmentModel? LastDuplicateModel { get; private set; }
        public bool Duplicate { get; set; }
        public bool DeferredModels { get; set; }
        public bool DeferredDuplicate { get; set; }
        public int? SaveResult { get; set; }
        public Exception? SaveException { get; set; }
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
            if (!DeferredDuplicate)
                return Task.FromResult(Duplicate);
            _duplicateCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _duplicateCompletion.Task;
        }

        public override Task<EquipmentModelUsage> GetUsageAsync(int id, int? newEquipmentTypeId = null) =>
            Task.FromResult(new EquipmentModelUsage());

        public override Task<int> SaveAsync(EquipmentModel model)
        {
            SaveCalls++;
            if (SaveException is not null)
                throw SaveException;
            if (SaveResult is not null)
                return Task.FromResult(SaveResult.Value);
            if (model.Id == 0)
                model.Id = 42;
            model.ChangedById = 4;
            model.ChangedAt = new DateTime(2026, 11, 17, 12, 30, 0);
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

        public void WaitForDuplicate()
        {
            for (var attempt = 0; attempt < 100 && _duplicateCompletion is null; attempt++)
                Thread.Sleep(10);
            Assert.NotNull(_duplicateCompletion);
        }

        public void CompleteDuplicate(bool value) => _duplicateCompletion!.TrySetResult(value);
    }
}
