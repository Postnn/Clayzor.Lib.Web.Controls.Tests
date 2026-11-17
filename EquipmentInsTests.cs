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

    /// <summary>MAC очищается от разделителей и проверяется по 12 hex-символам.</summary>
    [Fact]
    public void MacAddress_NormalizesAndValidates()
    {
        Assert.Equal("001122AABBCC", EquipmentEntity.NormalizeMacAddress("00:11-22 aa.bb.cc"));
        Assert.True(EquipmentEntity.IsValidMacAddress("00:11-22 aa.bb.cc"));
        Assert.False(EquipmentEntity.IsValidMacAddress("0011"));
    }

    /// <summary>Локальное время переводится в UTC со смещением на выбранную дату.</summary>
    [Theory]
    [InlineData(120, "2026-07-15T10:00:00Z")]
    [InlineData(60, "2026-01-15T11:00:00Z")]
    public void ApplyClientOffset_UsesRecordedDateOffset(int offset, string expected)
    {
        var local = new DateTime(2026, offset == 120 ? 7 : 1, 15, 12, 0, 0);
        var utc = EquipmentIns.ApplyClientOffset(local, offset);
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    /// <summary>Применить строит URL будущей формы редактирования со статическим id.</summary>
    [Fact]
    public void BuildEditUrl_UsesConfiguredFormAndStaticId() =>
        Assert.Equal("http://localhost:5020/Equipment/EquipmentForm?id=123",
            EquipmentIns.BuildEditUrl("http://localhost:5020/Equipment/EquipmentForm", 123));

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

        await page.InvokeAsync(() => FindButton(page, "OK").Click());

        page.WaitForAssertion(() => Assert.Equal(1, fixture.Service.CreateCalls), TimeSpan.FromSeconds(3));
        Assert.Equal(103, fixture.Service.Created!.EquipmentTypeId);
        Assert.Equal(945, fixture.Service.Created.ModelId);
        Assert.Equal(1, fixture.Service.Created.OwnerId);
        Assert.Equal(1, fixture.Service.Created.LocationId);
        Assert.Equal("CLAY_TEST_Description", fixture.Service.Created.Description);
        Assert.Equal(DateTimeKind.Utc, fixture.Service.Created.ReceivedAtUtc.Kind);
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

    /// <summary>При доступном бухучёте арендатор и бухгалтерский документ присутствуют с подсказками.</summary>
    [Fact]
    public async Task AccountingAvailable_ShowsTenantAndDocumentPlaceholders()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", accountingAvailable: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll("input[placeholder='Введите арендатора']"));
            Assert.Equal(2, page.FindAll("input[placeholder='Введите документ поступления']").Count);
        }, TimeSpan.FromSeconds(3));
    }

    private static Fixture CreateFixture(string uri, bool accountingAvailable = false)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(180);
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo(uri);
        var configuration = new ConfigurationManager
        {
            ["URI_equipment_form"] = "http://localhost:5020/Equipment/EquipmentForm",
        };
        var service = new FakeEquipmentInsService(CreateDb()) { AccountingAvailable = accountingAvailable };
        var modelSource = new EquipmentModelSelectDataSource(CreateDb(Script.Rows(ModelRow())));
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton<EquipmentInsService>(service);
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(CreateDb(Script.Rows(TypeRow()))));
        context.Services.AddSingleton(modelSource);
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PersonSelectDataSource(CreateDb(Script.Rows(PersonRow()))));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb(Script.Rows(LocationRow()))));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("receipt", new DocumentSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("accounting", new DocumentSelectDataSource(CreateDb()));
        return new Fixture(context, service);
    }

    private static DbManager CreateDb(params ScriptEntry[] entries) =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>(entries)));

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<EquipmentIns> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private static Dictionary<string, object?> TypeRow() => Script.Row(
        ("КодТипаОборудования", 103), ("ТипОборудования", "Шторы"), ("SNобязателен", (byte)0));

    private static Dictionary<string, object?> ModelRow() => Script.Row(
        ("КодМоделиОборудования", 945), ("МодельОборудования", "Шторы"),
        ("КодТипаОборудования", 103), ("Net", 0));

    private static Dictionary<string, object?> PersonRow() => Script.Row(
        ("КодЛица", 1), ("Кличка", "Тестовая организация"));

    private static Dictionary<string, object?> LocationRow() => Script.Row(
        ("КодРасположения", 1), ("РасположениеPath1", "CLAY_TEST / Room"));

    private sealed record Fixture(BunitContext Context, FakeEquipmentInsService Service);

    private sealed class FakeEquipmentInsService : EquipmentInsService
    {
        public FakeEquipmentInsService(DbManager db) : base(db) { }
        public EquipmentEntity? Created { get; private set; }
        public Exception? SaveException { get; set; }
        public bool AccountingAvailable { get; set; }
        public int CreateCalls { get; private set; }

        public override Task<EquipmentEntity?> GetForCopyAsync(int id) => Task.FromResult<EquipmentEntity?>(
            new EquipmentEntity { Id = id, EquipmentTypeId = 103, ModelId = 945, OwnerId = 1, LocationId = 1 });

        public override Task<bool> HasRoleAsync(int roleId) => Task.FromResult(true);
        public override Task<bool> CanUseAccountingAsync(int? personId) => Task.FromResult(AccountingAvailable);

        public override Task<int> CreateAsync(EquipmentEntity equipment)
        {
            CreateCalls++;
            if (SaveException is not null)
                throw SaveException;
            Created = equipment;
            return Task.FromResult(123);
        }
    }
}
