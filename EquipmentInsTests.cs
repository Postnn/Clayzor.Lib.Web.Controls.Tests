using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Documents;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
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

/// <summary>Профильные тесты создания оборудования.</summary>
public sealed class EquipmentInsTests
{
    /// <summary>Параметры соответствуют контракту sp_Оборудование_Ins.</summary>
    [Fact]
    public void InsertParameters_MatchStoredProcedureContract()
    {
        var equipment = new EquipmentEntity
        {
            SerialNumber = "SN", ModelId = 945, MacAddress = "001122334455",
            MacAddress2 = "66778899AABB", MacAddressIlo = "CCDDEEFF0011",
            NetworkName = "host", OwnerId = 1, TenantId = 2, DocumentReceiptOuId = 3,
            ShipperId = 4, Description = "notes", ReceivedAtUtc = new DateTime(2026, 7, 15, 10, 0, 0),
            LocationId = 1, LocationDescription = "location", EmployeeId = 4,
            EmployeeDescription = "employee", IsAccounting = true, InventoryNumber = "INV",
            AccountingDocumentId = 5, AccountingDescription = "accounting",
        };

        var parameters = equipment.CreateInsertParameters();

        Assert.Equal(21, parameters.ParameterNames.Count());
        Assert.Equal("SN", parameters.Get<string>("@SN"));
        Assert.Equal(945, parameters.Get<int>("@КодМоделиОборудования"));
        Assert.Equal(1, parameters.Get<int>("@КодЛицаВладельца"));
        Assert.Equal(1, parameters.Get<int>("@КодРасположения"));
        Assert.Equal(1, parameters.Get<int>("@ПринятьКБухгалтерскомуУчету"));
        Assert.Equal(5, parameters.Get<int>("@КодДокументаПоступления"));
        Assert.Contains("КодОборудования", parameters.ParameterNames);
    }

    /// <summary>Необязательные строки передаются процедуре пустыми, как требуют NOT NULL колонки.</summary>
    [Fact]
    public void InsertParameters_NullStringsBecomeEmpty()
    {
        var parameters = new EquipmentEntity().CreateInsertParameters();
        Assert.Equal(string.Empty, parameters.Get<string>("@MACадрес"));
        Assert.Equal(string.Empty, parameters.Get<string>("@MACадрес2"));
        Assert.Equal(string.Empty, parameters.Get<string>("@СетевоеИмя"));
        Assert.Equal(string.Empty, parameters.Get<string>("@Примечания"));
    }

    /// <summary>MAC принимает форматированный ввод и отклоняет не-hex символы.</summary>
    [Fact]
    public void MacAddress_NormalizesAndValidates()
    {
        Assert.Equal("001122334455", EquipmentEntity.NormalizeMacAddress("00:11:22:33:44:55"));
        Assert.Equal("001122AABBCC", EquipmentEntity.NormalizeMacAddress("00:11-22 aa.bb.cc"));
        Assert.True(EquipmentEntity.IsValidMacAddress("00:11:22:33:44:55"));
        Assert.True(EquipmentEntity.IsValidMacAddress("00:11-22 aa.bb.cc"));
        Assert.False(EquipmentEntity.IsValidMacAddress("0011"));
        Assert.False(EquipmentEntity.IsValidMacAddress("ZZZZ"));
        Assert.False(EquipmentEntity.IsValidMacAddress("00:11:22:33:44:5Z"));
        var options = (Clayzor.Lib.Web.Controls.Components.Form.ClayTextBoxOptions)typeof(EquipmentIns)
            .GetField("_macOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(new EquipmentIns())!;
        Assert.Equal(17, options.MaxLength);
    }

    /// <summary>Применить строит URL будущей формы редактирования со статическим id.</summary>
    [Fact]
    public void BuildEditUrl_UsesConfiguredFormAndStaticId() =>
        Assert.Equal("http://localhost:5020/Equipment/EquipmentForm?id=123",
            EquipmentIns.BuildEditUrl("http://localhost:5020/Equipment/EquipmentForm", 123));

    /// <summary>Выбранный ответственный открывает карточку сотрудника по настроенному URL.</summary>
    [Fact]
    public async Task ResponsibleViewItem_OpensConfiguredEmployeeCard()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        var select = page.FindComponent<Clayzor.Lib.Web.Controls.Components.Select.ClaySelect<Employee, int?>>();
        Assert.NotNull(select.Instance.ViewItem);
        await page.InvokeAsync(() => select.Instance.ViewItem!(new Employee { Id = 4 }));
        var invocation = Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "clayOpen");
        Assert.Equal("http://localhost/Users/UserForm?id=4", invocation.Arguments[0]);
        Assert.Equal("_blank", invocation.Arguments[1]);
    }

    /// <summary>SQL копирования восстанавливает тип, модель, владельца и расположение через Dapper-маппер.</summary>
    [Fact]
    public async Task CopyQuery_MapsAllLegacyFields()
    {
        DapperColumnMapper.Initialize();
        var db = CreateDb(Script.Rows(Script.Row(
            ("КодТипаОборудования", 103), ("КодМоделиОборудования", 945),
            ("КодЛицаВладельца", 1), ("КодРасположения", 2))));
        var service = new EquipmentInsService(db);
        var copy = await service.GetForCopyAsync(3);
        Assert.NotNull(copy);
        Assert.Equal(103, copy.EquipmentTypeId);
        Assert.Equal(945, copy.ModelId);
        Assert.Equal(1, copy.OwnerId);
        Assert.Equal(2, copy.LocationId);
    }

    /// <summary>Форма копирует исходные значения и успешно создаёт запись через сервис.</summary>
    [Fact]
    public async Task CopyAndOk_CreatesEquipmentAndClosesWindow()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        Assert.All(page.FindAll(".clay-select-input"), input =>
            Assert.False(string.IsNullOrWhiteSpace(input.GetAttribute("placeholder"))));
        page.Find("textarea").Input("CLAY_TEST_Description");

        await InvokeSave(page, true);

        page.WaitForAssertion(() => Assert.Equal(1, fixture.Service.CreateCalls), TimeSpan.FromSeconds(3));
        Assert.Equal(103, fixture.Service.Created!.EquipmentTypeId);
        Assert.Equal(945, fixture.Service.Created.ModelId);
        Assert.Equal(1, fixture.Service.Created.OwnerId);
        Assert.Equal(1, fixture.Service.Created.LocationId);
        Assert.Equal("CLAY_TEST_Description", fixture.Service.Created.Description);
        Assert.Equal(DateTimeKind.Utc, fixture.Service.Created.ReceivedAtUtc.Kind);
        Assert.Contains(context.JSInterop.Invocations,
            call => call.Identifier == "clayGridTimeZone.localDateTimeToUtcMilliseconds");
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>SQL-ошибка не закрывает форму и сохраняет введённые данные.</summary>
    [Fact]
    public async Task SaveSqlException_KeepsFormOpenAndInput()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        fixture.Service.SaveException = SqlExceptionFactory.Create(50000, "trigger");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        Assert.All(page.FindAll(".clay-select-input"), input =>
            Assert.False(string.IsNullOrWhiteSpace(input.GetAttribute("placeholder"))));
        page.Find("textarea").Input("CLAY_TEST_Description");

        await page.InvokeAsync(() => FindButton(page, "OK").Click());

        page.WaitForAssertion(() => Assert.Contains("Оборудование не создано", page.Markup), TimeSpan.FromSeconds(3));
        Assert.Equal("CLAY_TEST_Description", page.Find("textarea").GetAttribute("value"));
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
    }

    /// <summary>Отсутствующий URL редактирования обнаруживается до вызова процедуры.</summary>
    [Fact]
    public async Task ApplyWithoutEditUrl_DoesNotCreate()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", includeEditUrl: false);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        page.Find("textarea").Input("Changed");
        await InvokeSave(page, false);
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Contains("URI_equipment_form", page.Markup);
    }

    /// <summary>После успешного создания повторный Save не вызывает процедуру повторно.</summary>
    [Fact]
    public async Task SecondSaveAfterSuccess_OnlyNavigatesOrCloses()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        page.Find("textarea").Input("Changed");
        await InvokeSave(page, false);
        Assert.Equal(1, fixture.Service.CreateCalls);
        Assert.EndsWith("/Equipment/EquipmentForm?id=123", fixture.Navigation.Uri);
        await InvokeSave(page, true);
        Assert.Equal(1, fixture.Service.CreateCalls);
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Временный null и Escape не очищают зависимые значения типа и владельца.</summary>
    [Fact]
    public async Task TemporaryNullAndEscape_PreserveDependentValues()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", accountingAvailable: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        var model = CurrentModel(page.Instance);
        model.NetworkName = "host";
        model.MacAddress = "001122334455";
        model.DocumentReceiptOuId = 10;
        model.ShipperId = 4;
        model.IsAccounting = true;
        model.TenantId = 2;

        await InvokeHandler(page, "OnTypeIdChanged", (object?)null);
        Assert.Equal(945, model.ModelId);
        Assert.Equal("host", model.NetworkName);
        Assert.Equal("001122334455", model.MacAddress);
        await InvokeHandler(page, "OnTypeIdChanged", 103);
        Assert.Equal(103, model.EquipmentTypeId);
        Assert.Equal(945, model.ModelId);

        await InvokeHandler(page, "OnOwnerChangedAsync", (object?)null);
        Assert.Equal(10, model.DocumentReceiptOuId);
        Assert.Equal(4, model.ShipperId);
        Assert.Equal(2, model.TenantId);
        Assert.True(model.IsAccounting);
        await InvokeHandler(page, "OnOwnerChangedAsync", 1);
        Assert.Equal(1, model.OwnerId);
        Assert.Equal(10, model.DocumentReceiptOuId);
        Assert.Equal(4, model.ShipperId);
        Assert.Equal(2, model.TenantId);
    }

    /// <summary>Новая модель сохраняет поддерживаемый MAC и очищает только лишние поля.</summary>
    [Fact]
    public async Task ModelChange_PreservesMacSupportedByNewModel()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        var model = CurrentModel(page.Instance);
        model.MacAddress = "001122334455";
        model.MacAddress2 = "66778899AABB";
        model.MacAddressIlo = "CCDDEEFF0011";

        await InvokeHandler(page, "OnModelIdChanged", 946);
        Assert.Equal("001122334455", model.MacAddress);
        await InvokeHandler(page, "OnModelChangedAsync", new EquipmentModel
        {
            Id = 946, EquipmentTypeId = 103, NetworkInterfaces = 1,
        });
        Assert.Equal("001122334455", model.MacAddress);
        Assert.Null(model.MacAddress2);
        Assert.Null(model.MacAddressIlo);
    }

    /// <summary>Поздняя автоподстановка документа не перезаписывает ручной выбор поставщика.</summary>
    [Fact]
    public async Task LateDocumentShipper_DoesNotOverwriteManualSelection()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        fixture.ReceiptDocuments.DeferredShipper = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        var pending = InvokeHandler(page, "OnReceiptDocumentChangedAsync", 10);
        fixture.ReceiptDocuments.WaitForShipperRequest();
        await InvokeHandler(page, "OnShipperChanged", 99);
        fixture.ReceiptDocuments.ShipperCompletion.SetResult(4);
        await pending;
        Assert.Equal(99, CurrentModel(page.Instance).ShipperId);
    }

    /// <summary>Смена copyId через навигацию перезагружает тот же экземпляр без ручного lifecycle-вызова.</summary>
    [Fact]
    public async Task CopyIdChange_ReloadsSameComponentInstance()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentIns?copyId=8");
        page.WaitForAssertion(() =>
        {
            Assert.Equal([7, 8], fixture.Service.CopyRequests);
            Assert.Equal("Copy8", CurrentModel(page.Instance).Description);
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(0, CurrentModel(page.Instance).Id);
    }

    /// <summary>Поздний ответ копии A после его полного завершения не меняет копию B.</summary>
    [Fact]
    public async Task LateCopyLoad_DoesNotPartiallyOverwriteNewCopy()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        fixture.Service.AccountingAvailable = false;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        fixture.Service.DeferredAccountingPersonId = 1;
        var loadA = InvokeHandler(page, "LoadAsync");
        fixture.Service.WaitForDeferredAccountingRequest();
        fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentIns?copyId=8");
        fixture.Service.WaitForSecondAccountingRequest();
        page.WaitForAssertion(() => Assert.Equal("Copy8", CurrentModel(page.Instance).Description),
            TimeSpan.FromSeconds(3));
        fixture.Service.DeferredAccountingCompletion.SetResult(true);
        await loadA;
        Assert.False(PrivateBool(page.Instance, "_accountingAvailable"));
        Assert.Equal("Copy8", CurrentModel(page.Instance).Description);
    }

    /// <summary>Бухучёт из обработчика владельца прежней копии не меняет новую копию с тем же владельцем.</summary>
    [Fact]
    public async Task OwnerAccountingFromPreviousCopy_DoesNotOverwriteNewCopy()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        fixture.Service.DeferredAccountingPersonId = 1;
        var ownerRequest = InvokeHandler(page, "OnOwnerChangedAsync", 1);
        fixture.Service.WaitForDeferredAccountingRequest();
        fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentIns?copyId=8");
        fixture.Service.WaitForSecondAccountingRequest();
        page.WaitForAssertion(() => Assert.Equal("Copy8", CurrentModel(page.Instance).Description),
            TimeSpan.FromSeconds(3));
        fixture.Service.DeferredAccountingCompletion.SetResult(true);
        await ownerRequest;
        Assert.False(PrivateBool(page.Instance, "_accountingAvailable"));
        Assert.Equal("Copy8", CurrentModel(page.Instance).Description);
    }

    /// <summary>Отмена источника после Dispose завершает загрузку без необработанного исключения.</summary>
    [Fact]
    public async Task DisposeDuringLoad_CancellationIsObserved()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        fixture.Service.DeferredCopy = true;
        var load = InvokeHandler(page, "LoadAsync");
        fixture.Service.WaitForCopyRequest();
        page.Instance.Dispose();
        fixture.Service.CopyCompletion.SetException(new OperationCanceledException());
        await load;
        Assert.True(PrivateBool(page.Instance, "_disposed"));
    }

    /// <summary>Escape до завершения первого запроса повторно запрашивает и восстанавливает бухучёт.</summary>
    [Fact]
    public async Task EscapeDuringOwnerAccountingRequest_RestoresAvailableAccounting()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns", accountingAvailable: true);
        fixture.Service.DeferredAccountingPersonId = 2;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));
        var first = InvokeHandler(page, "OnOwnerChangedAsync", 2);
        fixture.Service.WaitForDeferredAccountingRequest();
        await InvokeHandler(page, "OnOwnerChangedAsync", (object?)null);
        var restored = InvokeHandler(page, "OnOwnerChangedAsync", 2);
        fixture.Service.WaitForSecondAccountingRequest();
        await restored;
        fixture.Service.DeferredAccountingCompletion.SetResult(true);
        await first;
        Assert.True(PrivateBool(page.Instance, "_accountingAvailable"));
        var tenantRow = page.FindAll(".equipment-ins-accounting-row").Single(row => row.TextContent.Contains("Арендатор:"));
        var tenantInput = tenantRow.QuerySelector("input");
        Assert.NotNull(tenantInput);
        Assert.True(tenantInput.HasAttribute("disabled"));
        Assert.Null(tenantInput.GetAttribute("placeholder"));
    }

    /// <summary>При доступном бухучёте отключённые поля видны без подсказок, редактируемый документ сохраняет подсказку.</summary>
    [Fact]
    public async Task AccountingAvailable_HidesDisabledPlaceholders()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", accountingAvailable: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() =>
        {
            var tenantRow = page.FindAll(".equipment-ins-accounting-row").Single(row => row.TextContent.Contains("Арендатор:"));
            var tenantInput = tenantRow.QuerySelector("input");
            Assert.NotNull(tenantInput);
            Assert.True(tenantInput.HasAttribute("disabled"));
            Assert.Null(tenantInput.GetAttribute("placeholder"));
            Assert.Single(page.FindAll("input[placeholder='Введите документ поступления']"));
        }, TimeSpan.FromSeconds(3));
    }

    private static Fixture CreateFixture(string uri, bool accountingAvailable = false, bool includeEditUrl = true)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(180);
        context.JSInterop.Setup<long>("clayGridTimeZone.localDateTimeToUtcMilliseconds", _ => true).SetResult(0L);
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo(uri);
        var configuration = new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost/Users/UserForm",
        };
        if (includeEditUrl)
            configuration["URI_equipment_form"] = "http://localhost/Equipment/EquipmentForm";
        var service = new FakeEquipmentInsService(CreateDb()) { AccountingAvailable = accountingAvailable };
        var modelSource = new EquipmentModelSelectDataSource(CreateDb(
            Script.Rows(ModelRow()), Script.Rows(ModelRow()), Script.Rows(ModelRow())));
        var receiptDocuments = new FakeDocumentSelectDataSource(CreateDb());
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton<EquipmentInsService>(service);
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(CreateDb(
            Script.Rows(TypeRow()), Script.Rows(TypeRow()), Script.Rows(TypeRow()))));
        context.Services.AddSingleton(modelSource);
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PersonSelectDataSource(CreateDb(
            Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()))));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb(
            Script.Rows(LocationRow()), Script.Rows(LocationRow()), Script.Rows(LocationRow()))));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("receipt", receiptDocuments);
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("accounting", new DocumentSelectDataSource(CreateDb()));
        return new Fixture(context, service, navigation, receiptDocuments);
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<EquipmentIns> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private static EquipmentEntity CurrentModel(EquipmentIns page) =>
        (EquipmentEntity)typeof(EquipmentIns).GetField("_model",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;

    private static bool PrivateBool(EquipmentIns page, string name) =>
        (bool)typeof(EquipmentIns).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;

    private static Task InvokeSave(IRenderedComponent<EquipmentIns> page, bool close) =>
        InvokeHandler(page, "SaveAsync", close);

    private static async Task InvokeHandler(IRenderedComponent<EquipmentIns> page, string name, params object?[] args)
    {
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentIns).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, args) is Task task)
                await task;
        });
    }

    private static Dictionary<string, object?> TypeRow() => Script.Row(
        ("КодТипаОборудования", 103), ("ТипОборудования", "Шторы"), ("SNобязателен", (byte)0));

    private static Dictionary<string, object?> ModelRow() => Script.Row(
        ("КодМоделиОборудования", 945), ("МодельОборудования", "Шторы"),
        ("КодТипаОборудования", 103), ("Net", 0));

    private static Dictionary<string, object?> PersonRow() => Script.Row(
        ("КодЛица", 1), ("Кличка", "Тестовая организация"));

    private static Dictionary<string, object?> LocationRow() => Script.Row(
        ("КодРасположения", 1), ("РасположениеPath1", "CLAY_TEST / Room"));

    private sealed record Fixture(
        BunitContext Context,
        FakeEquipmentInsService Service,
        FakeNavigationManager Navigation,
        FakeDocumentSelectDataSource ReceiptDocuments);

    private sealed class FakeEquipmentInsService : EquipmentInsService
    {
        private readonly TaskCompletionSource _deferredAccountingRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondAccountingRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _copyRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deferredAccountingCalls;

        public FakeEquipmentInsService(DbManager db) : base(db) { }
        public EquipmentEntity? Created { get; private set; }
        public Exception? SaveException { get; set; }
        public bool AccountingAvailable { get; set; }
        public bool DeferredCopy { get; set; }
        public int? DeferredAccountingPersonId { get; set; }
        public int CreateCalls { get; private set; }
        public List<int> CopyRequests { get; } = [];
        public TaskCompletionSource<bool> DeferredAccountingCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EquipmentEntity?> CopyCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<EquipmentEntity?> GetForCopyAsync(int id)
        {
            CopyRequests.Add(id);
            if (DeferredCopy)
            {
                _copyRequested.TrySetResult();
                return CopyCompletion.Task;
            }
            return Task.FromResult<EquipmentEntity?>(new EquipmentEntity
            {
                Id = id, EquipmentTypeId = 103, ModelId = 945, OwnerId = 1, LocationId = 1,
                Description = $"Copy{id}",
            });
        }

        public void WaitForCopyRequest()
        {
            for (var attempt = 0; attempt < 100 && !_copyRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_copyRequested.Task.IsCompleted, "Отложенный запрос копии не был начат");
        }

        public override Task<bool> HasRoleAsync(int roleId) => Task.FromResult(true);

        public override Task<bool> CanUseAccountingAsync(int? personId)
        {
            if (personId is null)
                return Task.FromResult(false);
            if (DeferredAccountingPersonId == personId)
            {
                var call = ++_deferredAccountingCalls;
                if (call == 1)
                {
                    _deferredAccountingRequested.TrySetResult();
                    return DeferredAccountingCompletion.Task;
                }
                _secondAccountingRequested.TrySetResult();
            }
            return Task.FromResult(AccountingAvailable);
        }

        public void WaitForDeferredAccountingRequest()
        {
            for (var attempt = 0; attempt < 100 && !_deferredAccountingRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_deferredAccountingRequested.Task.IsCompleted, "Первый запрос бухучёта не был начат");
        }

        public void WaitForSecondAccountingRequest()
        {
            for (var attempt = 0; attempt < 100 && !_secondAccountingRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_secondAccountingRequested.Task.IsCompleted, "Повторный запрос бухучёта не был начат");
        }

        public override Task<int> CreateAsync(EquipmentEntity equipment)
        {
            CreateCalls++;
            if (SaveException is not null)
                throw SaveException;
            Created = equipment;
            return Task.FromResult(123);
        }
    }

    private sealed class FakeDocumentSelectDataSource : DocumentSelectDataSource
    {
        private readonly TaskCompletionSource _shipperRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeDocumentSelectDataSource(DbManager db) : base(db) { }
        public bool DeferredShipper { get; set; }
        public TaskCompletionSource<int?> ShipperCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<int?> GetSingleShipperIdAsync(int documentId, int ownerId)
        {
            if (!DeferredShipper)
                return Task.FromResult<int?>(4);
            _shipperRequested.TrySetResult();
            return ShipperCompletion.Task;
        }

        public void WaitForShipperRequest()
        {
            for (var attempt = 0; attempt < 100 && !_shipperRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_shipperRequested.Task.IsCompleted, "Запрос поставщика не был начат");
        }
    }
}
