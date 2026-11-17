using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Documents;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Persons;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Extensions;
using MudBlazor.Services;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Профильные тесты жизненного цикла формы редактирования оборудования.</summary>
public sealed class EquipmentFormTests
{
    /// <summary>Применить сохраняет запись, обновляет opener и не повторяет UPDATE без изменений.</summary>
    [Fact]
    public async Task Apply_SavesOnceAndRefreshesOpener()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_UPDATED");

        await InvokeSave(page, false);
        await InvokeSave(page, false);

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.Equal("CLAY_TEST_UPDATED", fixture.Service.Updated?.SerialNumber);
        Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>SQL-ошибка сохраняет ввод и не выполняет success-path.</summary>
    [Fact]
    public async Task SaveSqlException_KeepsInputAndSkipsSuccessCallbacks()
    {
        var fixture = CreateFixture();
        fixture.Service.SaveException = SqlExceptionFactory.Create(50000, "trigger");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_FAILED");

        await InvokeSave(page, false);

        page.WaitForAssertion(() => Assert.Contains("Оборудование не сохранено", page.Markup));
        Assert.Equal("CLAY_TEST_FAILED", FindSn(page).GetAttribute("value"));
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Параллельный повторный Save не вызывает второй UPDATE.</summary>
    [Fact]
    public async Task ConcurrentSave_ExecutesSingleUpdate()
    {
        var fixture = CreateFixture();
        fixture.Service.DeferredUpdate = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_RACE");

        var first = InvokeSave(page, false);
        fixture.Service.WaitForUpdateRequest();
        var second = InvokeSave(page, false);
        fixture.Service.UpdateCompletion.SetResult();
        await first;
        await second;

        Assert.Equal(1, fixture.Service.UpdateCalls);
    }

    /// <summary>Смена id на том же маршруте загружает новую запись без ручного lifecycle-вызова.</summary>
    [Fact]
    public async Task QueryIdChange_LoadsNewEquipmentThroughRouter()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));

        WaitForSn(page, "CLAY_TEST_8");
        Assert.Equal(new[] { 7, 8 }, fixture.Service.Requests);
    }

    /// <summary>Для обычного оборудования дата списания требует документ, для SIM — нет.</summary>
    [Fact]
    public async Task WriteOffValidation_EnforcesDocumentExceptForSim()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_writeOffDate", new DateTime(2026, 3, 29));

        Assert.False(ValidateBusinessRules(page.Instance));
        CurrentModel(page.Instance).EquipmentTypeId = 27;
        Assert.True(ValidateBusinessRules(page.Instance));
        CurrentModel(page.Instance).DocumentWriteOffId = 9;
        SetPrivate(page.Instance, "_writeOffDate", null);
        Assert.False(ValidateBusinessRules(page.Instance));
    }

    /// <summary>Завершение отложенной загрузки после Dispose не меняет состояние формы.</summary>
    [Fact]
    public async Task DisposeDuringLoad_LateResultIsIgnored()
    {
        var fixture = CreateFixture();
        fixture.Service.DeferredInitialLoad = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        fixture.Service.WaitForGetRequest();

        page.Instance.Dispose();
        fixture.Service.InitialLoadCompletion.SetResult();
        await fixture.Service.InitialLoadReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await page.InvokeAsync(() => { });

        Assert.True(PrivateBool(page.Instance, "_disposed"));
        Assert.False(PrivateBool(page.Instance, "_loaded"));
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    /// <summary>Обновление текущих detail-метаданных не меняет признак несохранённой основной формы.</summary>
    [Fact]
    public async Task DetailMetadataChanges_DoNotMarkMainFormModified()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.False(IsModified(page.Instance));
        CurrentModel(page.Instance).LocationId = 4;
        CurrentModel(page.Instance).EmployeeId = 4;

        Assert.False(IsModified(page.Instance));
    }

    /// <summary>Добавление и удаление номера используют атомарный phone-save и синхронизацию.</summary>
    [Fact]
    public async Task PhoneAddAndDelete_UsesAtomicDiffAndSynchronizes()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_phoneToAdd", 11);
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentForm).GetMethod("AddPhoneAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, null) is Task task)
                await task;
        });
        page.WaitForAssertion(() => Assert.Contains("101", page.Markup));
        Assert.Equal(8, ((List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!)[0].ConnectionEquipmentId);

        await InvokeSave(page, false);
        Assert.Equal(1, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(1, fixture.Service.SyncCalls);
        Assert.Equal(new[] { 11 }, fixture.Service.PhoneSaves[0].Current);

        var phones = (List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!;
        phones.Clear();
        await InvokeSave(page, false);

        Assert.Equal(2, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(2, fixture.Service.SyncCalls);
        Assert.Equal(new[] { 11 }, fixture.Service.PhoneSaves[1].Original);
        Assert.Empty(fixture.Service.PhoneSaves[1].Current);
    }

    private static Fixture CreateFixture(bool phones = false)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(180);
        context.JSInterop.Setup<long>("clayGridTimeZone.localDateTimeToUtcMilliseconds", _ => true).SetResult(0L);
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=7");
        var configuration = new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost/user",
            ["URI_model_form"] = "http://localhost/Equipment/ModelEquipmentForm",
        };
        var service = new FakeEquipmentFormService(CreateDb());
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton<EquipmentFormService>(service);
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(CreateDb(
            Script.Rows(TypeRow(phones)), Script.Rows(TypeRow(phones)), Script.Rows(TypeRow(phones)), Script.Rows(TypeRow(phones)))));
        context.Services.AddSingleton(new EquipmentModelSelectDataSource(CreateDb(
            Script.Rows(ModelRow()), Script.Rows(ModelRow()), Script.Rows(ModelRow()), Script.Rows(ModelRow()))));
        context.Services.AddSingleton(new PhonePoolSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PhoneNumberSelectDataSource(CreateDb(
            Script.Rows(PhoneNumberRow()), Script.Rows(PhoneNumberRow()), Script.Rows(PhoneNumberRow()))));
        context.Services.AddSingleton(new PersonSelectDataSource(CreateDb(
            Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()))));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("receipt", new DocumentSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("writeoff", new DocumentSelectDataSource(CreateDb()));
        return new Fixture(context, service, navigation);
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));

    private static AngleSharp.Dom.IElement FindSn(IRenderedComponent<EquipmentForm> page) =>
        page.FindAll("input").Single(input => input.GetAttribute("value")?.StartsWith("CLAY_TEST") == true);

    private static void WaitForSn(IRenderedComponent<EquipmentForm> page, string value) =>
        page.WaitForAssertion(() => Assert.Equal(value, FindSn(page).GetAttribute("value")), TimeSpan.FromSeconds(3));

    private static async Task InvokeSave(IRenderedComponent<EquipmentForm> page, bool close) =>
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentForm).GetMethod("SaveAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, new object[] { close }) is Task task)
                await task;
        });

    private static EquipmentEntity CurrentModel(EquipmentForm page) =>
        (EquipmentEntity)GetPrivate(page, "_model")!;

    private static object? GetPrivate(EquipmentForm page, string name) => typeof(EquipmentForm)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page);

    private static void SetPrivate(EquipmentForm page, string name, object? value) => typeof(EquipmentForm)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(page, value);

    private static bool PrivateBool(EquipmentForm page, string name) => (bool)GetPrivate(page, name)!;

    private static bool IsModified(EquipmentForm page) => (bool)typeof(EquipmentForm)
        .GetProperty("IsModified", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page)!;

    private static bool ValidateBusinessRules(EquipmentForm page) => (bool)typeof(EquipmentForm)
        .GetMethod("ValidateBusinessRules", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(page, null)!;

    private static Dictionary<string, object?> TypeRow(bool phones = false) => Script.Row(
        ("КодТипаОборудования", 103), ("ТипОборудования", "Шторы"), ("SNобязателен", (byte)0),
        ("ЕстьХарактеристикиМонитора", (byte)0), ("ЕстьТелефонныйНомер", phones ? (byte)1 : (byte)0),
        ("ЕстьMACадрес", (byte)0), ("ЕстьСетевоеИмя", (byte)0));

    private static Dictionary<string, object?> ModelRow() => Script.Row(
        ("КодМоделиОборудования", 945), ("МодельОборудования", "Шторы"),
        ("КодТипаОборудования", 103), ("Net", 0));

    private static Dictionary<string, object?> PhoneNumberRow() => Script.Row(
        ("КодТелефонногоНомера", 11), ("Телефон", "101"),
        ("КодТелефоннойСтанции", 2), ("XorcomName", ""));

    private static Dictionary<string, object?> PersonRow() => Script.Row(
        ("КодЛица", 1), ("Кличка", "Тестовая организация"));

    private sealed record Fixture(
        BunitContext Context,
        FakeEquipmentFormService Service,
        FakeNavigationManager Navigation);

    private sealed class FakeEquipmentFormService : EquipmentFormService
    {
        private readonly TaskCompletionSource _getRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _updateRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeEquipmentFormService(DbManager db) : base(db) { }
        public EquipmentEntity? Updated { get; private set; }
        public Exception? SaveException { get; set; }
        public bool DeferredUpdate { get; set; }
        public bool DeferredInitialLoad { get; set; }
        public int UpdateCalls { get; private set; }
        public List<int> Requests { get; } = [];
        public List<EquipmentPhoneLink> StoredPhones { get; private set; } = [];
        public List<(int[] Original, int[] Current)> PhoneSaves { get; } = [];
        public int UpdateWithPhonesCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public TaskCompletionSource UpdateCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialLoadCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialLoadReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<EquipmentEntity?> GetAsync(int id)
        {
            Requests.Add(id);
            if (DeferredInitialLoad && id == 7)
            {
                _getRequested.TrySetResult();
                await InitialLoadCompletion.Task;
                InitialLoadReturned.TrySetResult();
            }
            return new EquipmentEntity
            {
                Id = id, EquipmentTypeId = 103, ModelId = 945, OwnerId = 1, LocationId = 55,
                SerialNumber = $"CLAY_TEST_{id}", Description = $"Copy{id}",
                ChangedById = 4, ChangedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0),
            };
        }

        public override Task<bool> HasRoleAsync(int roleId) => Task.FromResult(true);

        public override async Task UpdateAsync(EquipmentEntity equipment)
        {
            UpdateCalls++;
            Updated = equipment;
            if (DeferredUpdate)
            {
                _updateRequested.TrySetResult();
                await UpdateCompletion.Task;
            }
            if (SaveException is not null)
                throw SaveException;
        }

        public override Task<int?> GetConnectionEquipmentIdAsync(int phoneId, int? employeeId, int locationId) =>
            Task.FromResult<int?>(8);

        public override Task<IReadOnlyList<EquipmentLocationPeriod>> GetLocationsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentLocationPeriod>>([]);

        public override Task<bool> HasCurrentResponsibleAsync(int equipmentId) => Task.FromResult(false);

        public override Task<IReadOnlyList<EquipmentPhoneLink>> GetPhonesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentPhoneLink>>(StoredPhones.ToList());

        public override Task UpdateWithPhonesAsync(
            EquipmentEntity equipment,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current)
        {
            UpdateWithPhonesCalls++;
            Updated = equipment;
            StoredPhones = current.ToList();
            PhoneSaves.Add((original.Select(item => item.PhoneId).ToArray(), current.Select(item => item.PhoneId).ToArray()));
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<string>> SynchronizePhonesAsync(
            int equipmentId,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current)
        {
            SyncCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public void WaitForGetRequest()
        {
            for (var attempt = 0; attempt < 100 && !_getRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_getRequested.Task.IsCompleted, "Отложенная загрузка не была начата");
        }

        public void WaitForUpdateRequest()
        {
            for (var attempt = 0; attempt < 100 && !_updateRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_updateRequested.Task.IsCompleted, "Отложенное сохранение не было начато");
        }
    }
}
