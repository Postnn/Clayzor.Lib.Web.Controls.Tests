using System.Security.Claims;
using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.JSInterop;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Documents;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.DBSelect.Persons;
using Clayzor.Lib.Web.DBSelect.Phones;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Профильные тесты создания оборудования.</summary>
public sealed partial class EquipmentInsTests
{
    private const string FixturePrincipal = @"TEST\operator";

    /// <summary>Прямой вызов без подтверждённого сотрудника не может отправить процедуру создания.</summary>
    [Fact]
    public async Task DirectCreateWithoutIdentityDoesNotDispatch()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([new ScriptEntry { AffectedRows = 1 }]),
            emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var equipment = new EquipmentEntity { EquipmentTypeId = 103, ModelId = 945, OwnerId = 1, LocationId = 1, ReceivedAtUtc = DateTime.UtcNow };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => equipment.InsertAsync(db, null, 4));
        Assert.Empty(connection.CommandLog);
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
    }

    /// <summary>Открытый разрешённый редактор теряет право создания при свежем отзыве роли или смене сотрудника.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task FreshCreateRejectsRevokedRoleOrChangedEmployee(int freshId)
    {
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Id", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
            Script.Rows(Script.Row(("Id", freshId))),
            freshId == 4 ? Script.Rows([]) : Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        ]);
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var service = new EquipmentInsService(db);
        var openedBy = await service.GetCurrentUserAsync(FixturePrincipal, default);
        Assert.NotNull(openedBy);
        Assert.True(openedBy.HasRole(Роли.Специалист_по_инвентаризации_оборудования));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateAsync(ValidEquipment(), FixturePrincipal, openedBy.Id, default));

        Assert.Equal(4, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
    }

    /// <summary>Сервер отказывает до процедуры при недопустимых обязательных данных или свежих настройках типа.</summary>
    [Theory]
    [InlineData("model")]
    [InlineData("date")]
    [InlineData("responsible")]
    [InlineData("serial")]
    [InlineData("type")]
    public async Task DirectCreateRejectsInvalidMandatoryState(string failure)
    {
        var equipment = ValidEquipment();
        if (failure == "model") equipment.ModelId = 0;
        if (failure == "date") equipment.ReceivedAtUtc = default;
        if (failure == "responsible") equipment.LocationId = (int)Расположения.На_руках;

        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Id", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        ]);
        if (failure is "serial" or "type")
        {
            entries.Enqueue(Script.Rows(Script.Row(("Id", 103), ("IsSerialNumberRequired", (byte)1))));
            entries.Enqueue(Script.Rows(Script.Row(("Id", 945), ("EquipmentTypeId", failure == "type" ? 55 : 103))));
        }
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => equipment.InsertAsync(db, FixturePrincipal, 4));
        Assert.Contains(failure switch
        {
            "model" => "тип, модель",
            "date" => "допустимую дату",
            "responsible" => "ответственного сотрудника",
            "serial" => "серийный номер",
            _ => "не соответствуют",
        }, error.Message);
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
    }

    /// <summary>Нужны и роль бухгалтера для владельца, и разрешающий признак учёта лица.</summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task AccountingRequiresOwnerRoleAndAccountingFlag(int rolePerson, bool checksFlag)
    {
        var equipment = ValidEquipment();
        equipment.IsAccounting = true;
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Id", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)),
                Script.Row(("RoleId", (int)Роли.Бухгалтер), ("PersonId", rolePerson))),
            Script.Rows(Script.Row(("Id", 103), ("IsSerialNumberRequired", (byte)0))),
            Script.Rows(Script.Row(("Id", 945), ("EquipmentTypeId", 103))),
        ]);
        if (checksFlag) entries.Enqueue(Script.Rows(Script.Row(("Value", 0))));
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => equipment.InsertAsync(db, FixturePrincipal, 4));

        Assert.Equal(checksFlag, connection.CommandLog.Any(command => command.Contains("fn_БухгалтерскийУчет", StringComparison.Ordinal)));
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
    }

    /// <summary>Поздняя правка не меняет команду, пока она ожидает подтверждения сотрудника.</summary>
    [Fact]
    public async Task CreateChecksTheSnapshotAcceptedBeforeIdentityRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = new Queue<ScriptEntry>([
            new ScriptEntry { Rows = [Script.Row(("Id", 4))], Started = started, Release = release },
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        ]);
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var equipment = ValidEquipment();
        equipment.OwnerId = null;
        var create = equipment.InsertAsync(db, FixturePrincipal, 4);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            equipment.OwnerId = 1;
        }
        finally { release.TrySetResult(); }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => create);
        Assert.Contains("владельца", error.Message);
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
    }

    /// <summary>Допустимая команда отправляется в прежнюю процедуру с теми же параметрами.</summary>
    [Fact]
    public async Task ValidCreateDispatchesTheExistingProcedureAfterFreshChecks()
    {
        var dispatch = new InvalidOperationException("Fixture reached insert procedure");
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Id", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
            Script.Rows(Script.Row(("Id", 103), ("IsSerialNumberRequired", (byte)0))),
            Script.Rows(Script.Row(("Id", 945), ("EquipmentTypeId", 103))),
            Script.Error(dispatch),
        ]);
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);

        Assert.Same(dispatch, await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidEquipment().InsertAsync(db, FixturePrincipal, 4)));
        Assert.Contains("sp_Оборудование_Ins", connection.CommandLog.Last());
        var sent = connection.ParameterLog.Last();
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("FROM ЛицаЗаказчики", StringComparison.Ordinal));
        Assert.Equal(945, sent["КодМоделиОборудования"]);
        Assert.Equal(1, sent["КодЛицаВладельца"]);
    }

    /// <summary>Без подтверждения контрольной точки процедура вообще не отправляется.</summary>
    [Fact]
    public async Task CreateDoesNotDispatchWhenCheckpointRefuses()
    {
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Id", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
            Script.Rows(Script.Row(("Id", 103), ("IsSerialNumberRequired", (byte)0))),
            Script.Rows(Script.Row(("Id", 945), ("EquipmentTypeId", 103))),
        ]);
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);

        await Assert.ThrowsAsync<JSException>(() => ValidEquipment().InsertAsync(db, FixturePrincipal, 4,
            beforeDispatch: (_, _) => throw new JSException("Не получено подтверждение")));
        Assert.Equal(4, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
    }

    /// <summary>Арендатор проверяется как действующий заказчик до начала создания.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateValidatesTenantAgainstTheCurrentCustomerScope(bool valid)
    {
        var dispatch = new InvalidOperationException("Fixture reached insert procedure");
        var entries = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Id", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
            Script.Rows(Script.Row(("Id", 103), ("IsSerialNumberRequired", (byte)0))),
            Script.Rows(Script.Row(("Id", 945), ("EquipmentTypeId", 103))),
            valid ? Script.Rows(Script.Row(("Id", 42), ("Name", "Заказчик"))) : Script.Rows([]),
            Script.Error(dispatch),
        ]);
        using var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var equipment = ValidEquipment();
        equipment.TenantId = 42;
        var checkpoint = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => equipment.InsertAsync(db, FixturePrincipal, 4,
            beforeDispatch: (_, _) => { checkpoint++; return Task.CompletedTask; }));

        var index = Array.FindIndex(connection.CommandLog.ToArray(), command => command.Contains("FROM ЛицаЗаказчики", StringComparison.Ordinal));
        Assert.True(index >= 0);
        Assert.Equal(42, connection.ParameterLog[index]["Id"]);
        Assert.Equal(valid ? 1 : 0, checkpoint);
        if (valid)
        {
            Assert.Same(dispatch, error);
            Assert.Contains("sp_Оборудование_Ins", connection.CommandLog.Last());
            Assert.Equal(42, connection.ParameterLog.Last()["КодЛицаАрендатора"]);
        }
        else
        {
            Assert.Contains("арендатора", error.Message);
            Assert.DoesNotContain(connection.CommandLog, command => command.Contains("sp_Оборудование_Ins", StringComparison.Ordinal));
        }
        Assert.Equal(42, equipment.TenantId);
    }

    /// <summary>Копия разрешает арендатора через заказчиков, сохраняя ID даже при недоступности имени.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopiedTenantUsesCustomerSourceWithoutChangingOwnerOrShipper(bool valid)
    {
        var customer = valid ? Script.Rows(Script.Row(("КодЛица", 42), ("Кличка", "Заказчик аренды"))) : Script.Rows([]);
        using var customerDb = CreateDb(customer, customer, customer);
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", accountingAvailable: true, customerDb: customerDb);
        fixture.Service.CopyTenantId = 42;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("[data-draft-key='ins.tenant']");
        var people = page.FindComponents<ClaySelect<PersonLookup, int?>>();
        Assert.IsType<PhoneCustomerSelectDataSource>(people.Single(p => p.Instance.Options?.DraftKey == "ins.tenant").Instance.DataSource);
        foreach (var key in new[] { "ins.owner", "ins.shipper" })
            Assert.IsType<PersonSelectDataSource>(people.Single(p => p.Instance.Options?.DraftKey == key).Instance.DataSource);
        page.WaitForAssertion(() => Assert.Equal(valid ? "Заказчик аренды" : "#42",
            page.Find("input[data-draft-key='ins.tenant']").GetAttribute("value")));
        Assert.Equal(42, CurrentModel(page.Instance).TenantId);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>Настроенные алиасы подключают поиск и просмотр без вымышленного маршрута.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelAndLocationActionsUseOnlyConfiguredAliases(bool configureSearch)
    {
        var settings = new Dictionary<string, string?> { ["URI_model_form"] = "/Equipment/ModelEquipmentForm" };
        if (configureSearch)
        {
            settings["URI_model_search"] = "/configured/models";
            settings["URI_location_search"] = "/configured/locations";
        }
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", lookupSettings: settings);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("textarea");

        var model = page.FindComponent<ClaySelect<EquipmentModel, int?>>().Instance;
        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>().Instance;
        Assert.NotNull(model.ViewItem);
        Assert.Equal(configureSearch, model.AdvancedSearch is not null);
        Assert.Equal(configureSearch, location.AdvancedSearch is not null);
        Assert.Equal(configureSearch, location.ViewItem is not null);

        await page.InvokeAsync(() => model.ViewItem(new EquipmentModel { Id = 945 }));
        if (location.ViewItem is not null)
            await page.InvokeAsync(() => location.ViewItem(new LocationLookup { Id = 1 }));
        var calls = context.JSInterop.Invocations["clayOpen"];
        Assert.Contains(calls, call => Assert.IsType<string>(call.Arguments[0]).EndsWith("ModelEquipmentForm?id=945", StringComparison.Ordinal));
        if (configureSearch)
            Assert.Contains(calls, call => Assert.IsType<string>(call.Arguments[0]).EndsWith("/configured/locations?id=1", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>ACK модели учитывает действующий тип, выбор и свежую роль.</summary>
    [Theory]
    [InlineData("accepted", "Accepted")]
    [InlineData("revoked", "Rejected")]
    [InlineData("scope", "Rejected")]
    [InlineData("stale", "Stale")]
    public async Task ModelSearchAcknowledgesOnlyCurrentAuthorizedType(string scenario, string expected)
    {
        var type = scenario == "scope" ? 107 : 103;
        var fixture = CreateFixture($"http://localhost/Equipment/EquipmentIns?codetype={type}", lookupSettings:
            new Dictionary<string, string?> { ["URI_model_search"] = "/configured/models?codetype=2&keep=1" });
        await using var context = fixture.Context;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.JSInterop.SetupVoid("claySelectAdvancedSearch.open", _ => { opened.TrySetResult(); return true; }).SetVoidResult();
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("textarea", TimeSpan.FromSeconds(5));
        var select = page.FindComponent<ClaySelect<EquipmentModel, int?>>().Instance;
        var applied = 0;
        var search = new ClaySelectAdvancedSearchContext
        {
            SearchText = "модель & тест",
            ApplyResult = (Func<ClaySelectExternalResult<EquipmentModel, int?>, CancellationToken, Task<ClaySelectReturnDecision>>)((result, _) =>
            {
                Assert.Equal(945, result.Item?.Id);
                applied++;
                return Task.FromResult(ClaySelectReturnDecision.Accepted);
            }),
        };
        var pending = page.InvokeAsync(() => select.AdvancedSearch!(search));
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var open = Assert.Single(context.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
            var address = new Uri(Assert.IsType<string>(open.Arguments[0]));
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(address.Query);
            Assert.Equal(type.ToString(), query["codetype"].ToString());
            Assert.Equal("1", query["keep"].ToString());
            Assert.Equal(search.SearchText, query["search"].ToString());
            Assert.True(Assert.IsType<bool>(open.Arguments[4]));

            if (scenario == "revoked") fixture.Service.AllowCreate = false;
            if (scenario == "stale") await InvokeHandler(page, "OnTypeIdChanged", 104);
            var service = context.Services.GetRequiredService<ClaySelectAdvancedSearchService>();
            var request = Assert.IsType<string>(open.Arguments[1]);
            var decision = await service.ApplyAsync(request, 1, JsonSerializer.SerializeToElement(945));
            Assert.Equal(expected, decision);
            Assert.Equal(expected == "Accepted" ? 1 : 0, applied);
            if (decision != "Accepted") await service.CancelAsync(request);
        }
        finally
        {
            var calls = context.JSInterop.Invocations["claySelectAdvancedSearch.open"];
            if (calls.Count == 1)
                await context.Services.GetRequiredService<ClaySelectAdvancedSearchService>().CancelAsync(Assert.IsType<string>(calls[0].Arguments[1]));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>Аналогичная область ACK у расположения не перехватывает новый локальный выбор.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationSearchAcknowledgesOnlyCurrentSelection(bool changeSelection)
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", lookupSettings:
            new Dictionary<string, string?> { ["URI_location_search"] = "/configured/locations" });
        await using var context = fixture.Context;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.JSInterop.SetupVoid("claySelectAdvancedSearch.open", _ => { opened.TrySetResult(); return true; }).SetVoidResult();
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("textarea");
        var select = page.FindComponent<ClaySelect<LocationLookup, int?>>().Instance;
        var applied = 0;
        var search = new ClaySelectAdvancedSearchContext
        {
            SearchText = "склад",
            ApplyResult = (Func<ClaySelectExternalResult<LocationLookup, int?>, CancellationToken, Task<ClaySelectReturnDecision>>)((result, _) =>
            {
                Assert.Equal(1, result.Item?.Id);
                applied++;
                return Task.FromResult(ClaySelectReturnDecision.Accepted);
            }),
        };
        var pending = page.InvokeAsync(() => select.AdvancedSearch!(search));
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var open = Assert.Single(context.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
            if (changeSelection) await InvokeHandler(page, "OnLocationChanged", 2);
            var service = context.Services.GetRequiredService<ClaySelectAdvancedSearchService>();
            var request = Assert.IsType<string>(open.Arguments[1]);
            var decision = await service.ApplyAsync(request, 1, JsonSerializer.SerializeToElement(1));
            Assert.Equal(changeSelection ? "Stale" : "Accepted", decision);
            Assert.Equal(changeSelection ? 0 : 1, applied);
            if (changeSelection) Assert.Equal(2, CurrentModel(page.Instance).LocationId);
        }
        finally
        {
            var calls = context.JSInterop.Invocations["claySelectAdvancedSearch.open"];
            if (calls.Count == 1)
                await context.Services.GetRequiredService<ClaySelectAdvancedSearchService>().CancelAsync(Assert.IsType<string>(calls[0].Arguments[1]));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    private static EquipmentEntity ValidEquipment() => new()
    {
        EquipmentTypeId = 103, ModelId = 945, OwnerId = 1, LocationId = 1, ReceivedAtUtc = DateTime.UtcNow,
    };

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

        await FindButton(page, "OK").ClickAsync();

        page.WaitForAssertion(() => Assert.Equal(1, fixture.Service.CreateCalls), TimeSpan.FromSeconds(3));
        Assert.Equal(103, fixture.Service.Created!.EquipmentTypeId);
        Assert.Equal(945, fixture.Service.Created.ModelId);
        Assert.Equal(1, fixture.Service.Created.OwnerId);
        Assert.Equal(1, fixture.Service.Created.LocationId);
        Assert.Equal("Copy7", fixture.Service.Created.Description);
        Assert.Equal(DateTimeKind.Utc, fixture.Service.Created.ReceivedAtUtc.Kind);
        Assert.Contains(context.JSInterop.Invocations,
            call => call.Identifier == "clayGridTimeZone.localDateTimeToUtcMilliseconds");
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Незаполненная форма без источника копирования закрывается без попытки записи.</summary>
    [Fact]
    public async Task UntouchedNewForm_ClosesWithoutCreatingEquipment()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")), TimeSpan.FromSeconds(3));

        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        await FindButton(page, "OK").ClickAsync();

        Assert.Equal(0, fixture.Service.CreateCalls);
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

        page.WaitForAssertion(() => Assert.Contains("Повтор создания заблокирован", page.Markup), TimeSpan.FromSeconds(3));
        Assert.Equal("CLAY_TEST_Description", page.Find("textarea").GetAttribute("value"));
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
    }

    /// <summary>Повторное открытие использует доказанный ID либо оставляет попытку заблокированной.</summary>
    [Theory]
    [InlineData("running", false, false)]
    [InlineData("running", true, false)]
    [InlineData("confirmed", false, false)]
    [InlineData("confirmed", true, false)]
    [InlineData("confirmed", true, true)]
    public async Task RemountUsesOnlyTheReceiptBoundToItsAttempt(string status, bool hasReceipt, bool mismatched)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId, kind = "equipment.create", status, revision = 6, data = "{}",
            scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        var scopes = hasReceipt ? new Dictionary<string, string>
        {
            ["creation-result"] = JsonSerializer.Serialize(new
            {
                Source = "7:0", AttemptId = attemptId, Revision = mismatched ? 7 : 6, EquipmentId = 321,
            }),
        } : null;
        var restore = new ClayDraftRestore("equipment-ins-remount", true, 7, null, operation, scopes);
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7", restoredCreation: restore);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        var proven = hasReceipt && !mismatched;
        page.WaitForAssertion(() => Assert.Contains(proven ? "№321" : "Повтор создания заблокирован", page.Markup));

        await InvokeSave(page, false);
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        if (proven)
        {
            await FindButton(page, "Завершить обработку черновика").ClickAsync();
            Assert.Equal(6L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
            Assert.Equal(status == "confirmed" ? 0 : 1, context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"].Count);
            await FindButton(page, "Открыть созданное оборудование").ClickAsync();
            Assert.Contains("id=321", fixture.Navigation.Uri);
            Assert.Equal(0, fixture.Service.CreateCalls);
        }
        else
        {
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Contains("Открыть созданное оборудование"));
        }
    }

    /// <summary>Потеря записи точного ID блокирует процедуру; локальный повтор сохраняет только подтверждение.</summary>
    [Fact]
    public async Task KnownResultRetriesReceiptBeforeCleanupAndNeverCreatesAgain()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var fail = true;
        context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", call =>
        {
            if (!fail || !Equals(call.Arguments[1], "creation-result")) return false;
            fail = false;
            return true;
        }).SetException(new JSException("Fixture receipt storage failed"));
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));

        await FindButton(page, "Применить").ClickAsync();
        Assert.Equal(1, fixture.Service.CreateCalls);
        Assert.Contains("№123", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        await FindButton(page, "Отмена").ClickAsync();
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Contains("сохранить номер созданного оборудования", page.Markup);
        await InvokeSave(page, false);
        Assert.Equal(1, fixture.Service.CreateCalls);

        await FindButton(page, "Завершить обработку черновика").ClickAsync();
        var stores = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]
            .Where(call => Equals(call.Arguments[1], "creation-result")).ToArray();
        Assert.Equal(2, stores.Length);
        Assert.Equal(stores[0].Arguments[2], stores[1].Arguments[2]);
        using var receipt = JsonDocument.Parse(Assert.IsType<string>(stores[1].Arguments[2]));
        using var intent = JsonDocument.Parse(Assert.IsType<string>(
            Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]).Arguments[1]));
        Assert.Equal(intent.RootElement.GetProperty("attemptId").GetString(), receipt.RootElement.GetProperty("AttemptId").GetString());
        Assert.Equal(123, receipt.RootElement.GetProperty("EquipmentId").GetInt32());
        Assert.Equal(6L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]).Arguments[1]);
        Assert.Equal(6L, Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
        Assert.Equal(1, fixture.Service.CreateCalls);
    }

    /// <summary>Новая запись и копия восстанавливают свой typed и точные ключи raw без повторного создания.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorDraftRoundTripsForNewAndCopy(bool copy)
    {
        var uri = "http://localhost/Equipment/EquipmentIns" + (copy ? "?copyId=7" : "");
        var payload = await CaptureEditorDraftAsync(uri);
        using var typed = JsonDocument.Parse(payload);
        Assert.Equal(copy ? "7:0" : "0:0", typed.RootElement.GetProperty("Source").GetString());
        Assert.NotEmpty(typed.RootElement.GetProperty("Original").GetString()!);
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(payload));
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();

        page.WaitForAssertion(() => Assert.Equal("Локальная правка", page.Find("textarea").TextContent));
        Assert.NotNull(page.Find("[data-draft-key='ins.notes']"));
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        Assert.False(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>Недоступная копия показывает свою правку; появление изменённого источника требует явного принятия.</summary>
    [Fact]
    public async Task MissingAndChangedCopyKeepsInputUntilExplicitRebase()
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var payload = await CaptureEditorDraftAsync(uri);
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(payload));
        fixture.Service.CopyAvailable = false;
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(8, []));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(true);
        var page = context.Render<EquipmentIns>();

        page.WaitForAssertion(() => Assert.Contains("Исходное оборудование недоступно", page.Markup));
        Assert.Equal("Локальная правка", page.Find("textarea").TextContent);
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        fixture.Service.CopyAvailable = true;
        fixture.Service.CopyDescription = "Свежая версия копии";
        await page.InvokeAsync(() => FindButton(page, "Обновить").ClickAsync());
        page.WaitForAssertion(() => Assert.Contains("Исходный контекст изменился", page.Markup));
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));

        await page.InvokeAsync(() => FindButton(page, "Принять актуальный исходный контекст").ClickAsync());
        Assert.Equal("Локальная правка", page.Find("textarea").TextContent);
        Assert.False(FindButton(page, "Применить").HasAttribute("disabled"));
        var rebase = Assert.Single(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        Assert.Equal(8L, rebase.Arguments[2]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(rebase.Arguments[4]));
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    /// <summary>Более поздний ввод с точным старым ID ждёт явного открытия другого создания.</summary>
    [Fact]
    public async Task LaterDraftWaitsForExplicitNewCreationAfterKnownId()
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var payload = await CaptureEditorDraftAsync(uri);
        var scopes = new Dictionary<string, string>
        {
            ["creation-result"] = JsonSerializer.Serialize(new
            {
                Source = "7:0", AttemptId = Guid.NewGuid().ToString("N"), Revision = 6, EquipmentId = 321,
            }),
            ["main"] = payload,
        };
        var fixture = CreateFixture(uri, restoredCreation: new ClayDraftRestore("completed", true, 12, null, null, scopes),
            afterReceiptDiscard: EditorRestore(payload));
        await using var context = fixture.Context;

        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("№321", page.Markup));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.Equal(0, fixture.Service.CreateCalls);

        await page.InvokeAsync(() => FindButton(page, "Начать другое создание").ClickAsync());
        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.register"].Count);
        Assert.Equal("Локальная правка", CurrentModel(page.Instance).Description);
        page.WaitForAssertion(() => Assert.Equal("Локальная правка", page.Find("textarea").TextContent));
        Assert.Equal("creation-result", Assert.Single(context.JSInterop.Invocations["clayDraftStore.discardScope"]).Arguments[1]);
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Невосстановленная сырая часть не позволяет зафиксировать SQL intent.</summary>
    [Fact]
    public async Task PendingRawBlocksCreationAfterTypedRecovery()
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var payload = await CaptureEditorDraftAsync(uri);
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(payload), pendingEditorRaw: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("Часть ввода ожидает восстановления", page.Markup));

        Assert.Equal("Локальная правка", page.Find("textarea").TextContent);
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        await InvokeSave(page, false);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>Отзыв права не раскрывает локальный typed/raw и не разрешает создание.</summary>
    [Fact]
    public async Task RevokedRoleDoesNotRestoreEditableDraft()
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var payload = await CaptureEditorDraftAsync(uri);
        var fixture = CreateFixture(uri, restoredCreation: EditorRestore(payload));
        fixture.Service.AllowCreate = false;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();

        page.WaitForAssertion(() => Assert.Contains("Для создания оборудования требуется роль", page.Markup));
        Assert.DoesNotContain("Локальная правка", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        await InvokeSave(page, false);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>Старый ответ справочника не заменяет более новый выбранный тип в редакторе.</summary>
    [Fact]
    public async Task ResolvedPreviousTypeDoesNotReplaceCurrentSelection()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("textarea");
        await InvokeHandler(page, "OnTypeIdChanged", 107);
        await InvokeHandler(page, "OnTypeChanged", new EquipmentType { Id = 103 });
        Assert.Equal(107, CurrentModel(page.Instance).EquipmentTypeId);
    }

    /// <summary>Новый выбор модели того же типа дожидается своих метаданных, даже если предыдущий запрос уже записал код типа.</summary>
    [Fact]
    public async Task NewModelOfSamePendingTypeDoesNotUseStaleRules()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var typeRow = TypeRow();
        typeRow["ЕстьСетевоеИмя"] = (byte)1;
        using var typeDb = CreateDb(
            new ScriptEntry { Rows = [typeRow], Started = entered, Release = release },
            Script.Rows(typeRow));
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns", typeDb: typeDb);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForElement("textarea");
        await InvokeHandler(page, "OnModelIdChanged", 946);
        var previous = InvokeHandler(page, "OnModelChangedAsync", new EquipmentModel
        {
            Id = 946, EquipmentTypeId = 103,
        });

        Task latest;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CurrentModel(page.Instance).NetworkName = "keep-host";
            await InvokeHandler(page, "OnModelIdChanged", 947);
            latest = InvokeHandler(page, "OnModelChangedAsync", new EquipmentModel
            {
                Id = 947, EquipmentTypeId = 103,
            });
            await page.InvokeAsync(() => { });
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(previous, latest).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(947, CurrentModel(page.Instance).ModelId);
        Assert.Equal("keep-host", CurrentModel(page.Instance).NetworkName);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    private static ClayDraftRestore EditorRestore(string payload) => new("equipment-ins-editor", true, 8, null, null,
        new Dictionary<string, string> { ["main"] = payload });

    private static async Task<string> CaptureEditorDraftAsync(string uri)
    {
        var fixture = CreateFixture(uri);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        var editor = page.WaitForElement("textarea[data-draft-key='ins.notes']", TimeSpan.FromSeconds(5));
        await editor.InputAsync("Локальная правка");
        page.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "main") && ReadDraftNotes(Assert.IsType<string>(call.Arguments[2])) == "Локальная правка"),
            TimeSpan.FromSeconds(5));
        return Assert.IsType<string>(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]
            .Last(call => Equals(call.Arguments[1], "main")).Arguments[2]);
    }
    private static string? ReadDraftNotes(string payload)
    {
        using var draft = JsonDocument.Parse(payload);
        return draft.RootElement.GetProperty("Model").GetProperty("Description").GetString();
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
        Assert.False(FindButton(page, "Применить").HasAttribute("disabled"));
        await FindButton(page, "Применить").ClickAsync();
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
        await page.Instance.DisposeAsync();
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
            Assert.Single(page.FindAll("input[placeholder='Выберите документ поступления']"));
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>Выход из новой формы учитывает raw и применяет только явно выбранный исход.</summary>
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    public async Task CancelRawOnlyEditorRespectsThreeWayChoice(string choice)
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?codetype=103");
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(9, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Equal(103, CurrentModel(page.Instance).EquipmentTypeId));
        var cancel = page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        const string reset = "Отменить мои изменения и начать заново";
        ChooseInsCancelAction(dialogs, choice == "continue" ? "Продолжить редактирование" :
            choice == "close" ? "Закрыть без сохранения" : reset, reset);
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Empty(fixture.Service.CopyRequests);
        Assert.Equal(choice == "reset" ? 2 : 1, fixture.Service.IdentityChecks);
        if (choice == "continue")
        {
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
            return;
        }

        var replacement = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal("main", replacement.Arguments[1]);
        Assert.Equal(9L, replacement.Arguments[2]);
        if (choice == "close")
        {
            Assert.Null(replacement.Arguments[3]);
            Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        }
        else
        {
            Assert.NotNull(replacement.Arguments[3]);
            Assert.Equal(103, CurrentModel(page.Instance).EquipmentTypeId);
            Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
            Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        }
    }

    /// <summary>Сброс копии использует свежий источник и исходную ревизию диалога.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("late")]
    public async Task CopyCancelResetReadsBeforeRevisionCas(string mode)
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var fixture = CreateFixture(uri);
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(17, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(mode != "late");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));
        CurrentModel(page.Instance).Description = "Local copy";
        var cancel = page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Contains("Несохранённые изменения", dialogs.Markup));
        fixture.Service.CopyDescription = "Fresh copy";
        fixture.Service.CopyAvailable = mode != "missing";
        fixture.Service.AllowCreate = mode != "denied";
        ChooseInsCancelAction(dialogs, "Отменить мои изменения и загрузить из БД");
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(uri, fixture.Navigation.Uri);
        Assert.Equal(0, fixture.Service.CreateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(mode == "denied" ? [7] : new[] { 7, 7 }, fixture.Service.CopyRequests);
        if (mode is "late" or "success")
        {
            var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(17L, replace.Arguments[2]);
            using var baseline = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
            Assert.Equal("Fresh copy", baseline.RootElement.GetProperty("Model").GetProperty("Description").GetString());
        }
        else
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(mode == "success" ? "Fresh copy" : "Local copy", CurrentModel(page.Instance).Description);
    }

    /// <summary>Подтверждает сохранность отправленной попытки при закрытии и отложенную навигацию при raw.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationExitKeepsUnknownAndUsesGuardForInternalNavigation(bool unknown)
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var operation = unknown
            ? JsonSerializer.Serialize(new { version = 1, attemptId = Guid.NewGuid().ToString("N"), kind = "equipment.create",
                status = "unknown", revision = 6, data = "{}", scopes = Array.Empty<object>(),
                acceptLegacyTyped = false, legacyTypedRevision = 0 })
            : null;
        var fixture = CreateFixture(uri, restoredCreation: operation is null ? null :
            new ClayDraftRestore("unknown-fixture", true, 7, null, operation));
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(7, true, unknown));
        var navigation = new InsGuardNavigation(uri);
        context.Services.AddSingleton<NavigationManager>(navigation);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Отмена").HasAttribute("disabled")));
        if (unknown)
        {
            await FindButton(page, "Отмена").ClickAsync();
            Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
            Assert.Contains("Повтор создания заблокирован", page.Markup);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        }
        else
        {
            var navigating = page.InvokeAsync(() =>
                navigation.CanNavigateAsync("http://localhost/Equipment/EquipmentIns?copyId=8"));
            ChooseInsCancelAction(dialogs, "Продолжить редактирование");
            Assert.False(await navigating.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(uri, navigation.Uri);
            Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        }
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    /// <summary>Поздний ответ чтения предыдущей копии не сбрасывает соседний редактор.</summary>
    [Fact]
    public async Task CopyResetDoesNotReplacePreviousRouteAfterNewSourceOpens()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(17, true, false));
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));
        fixture.Service.DeferredCopy = true;
        var cancel = page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        ChooseInsCancelAction(dialogs, "Отменить мои изменения и загрузить из БД");
        fixture.Service.WaitForCopyRequest();
        fixture.Service.DeferredCopy = false;

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentIns?copyId=8"));
        page.WaitForAssertion(() => Assert.Equal("Copy8", CurrentModel(page.Instance).Description));
        fixture.Service.CopyCompletion.TrySetResult(new EquipmentEntity { Id = 7, Description = "Late Copy7" });
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Copy8", CurrentModel(page.Instance).Description);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    private sealed class InsGuardNavigation : NavigationManager
    {
        public InsGuardNavigation(string uri) => Initialize("http://localhost/", uri);

        protected override void SetNavigationLockState(bool value) { }

        public Task<bool> CanNavigateAsync(string target) =>
            NotifyLocationChangingAsync(target, null, false).AsTask();
    }

    private static void ChooseInsCancelAction(IRenderedComponent<MudDialogProvider> dialogs, string choice, string? extra = null)
    {
        dialogs.WaitForAssertion(() => Assert.Contains(choice, dialogs.Markup));
        if (extra is not null) Assert.Contains(extra, dialogs.Markup);
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == choice).Click();
    }

    private static Fixture CreateFixture(string uri, bool accountingAvailable = false, bool includeEditUrl = true,
        ClayDraftRestore? restoredCreation = null, bool pendingEditorRaw = false, ClayDraftRestore? afterReceiptDiscard = null, DbManager? typeDb = null, DbManager? customerDb = null, IReadOnlyDictionary<string, string?>? lookupSettings = null)
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
        if (lookupSettings is not null)
            foreach (var setting in lookupSettings) configuration[setting.Key] = setting.Value;
        var service = new FakeEquipmentInsService(CreateDb()) { AccountingAvailable = accountingAvailable };
        var modelSource = new EquipmentModelSelectDataSource(CreateDb(
            Script.Rows(ModelRow()), Script.Rows(ModelRow()), Script.Rows(ModelRow())));
        var receiptDocuments = new FakeDocumentSelectDataSource(CreateDb());
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClaySelectAdvancedSearchService>();
        bool ReceiptDiscarded() => context.JSInterop.Invocations["clayDraftStore.discardScope"].Count != 0;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => afterReceiptDiscard is null || !ReceiptDiscarded())
            .SetResult(restoredCreation ?? new ClayDraftRestore("equipment-ins-test", false, 0, null, null));
        if (afterReceiptDiscard is not null)
            context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => ReceiptDiscarded())
                .SetResult(afterReceiptDiscard);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(0, false, false));
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([]);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], pendingEditorRaw ? ["data:ins.notes:0"] : []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(!pendingEditorRaw, pendingEditorRaw ? ["main"] : [], []));
        Action<string>? completeBegin = null;
        var begin = context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", invocation =>
        {
            using var intent = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[1]));
            var root = intent.RootElement;
            completeBegin!(JsonSerializer.Serialize(new
            {
                version = 1,
                attemptId = root.GetProperty("attemptId").GetString(),
                kind = root.GetProperty("kind").GetString(),
                status = "running",
                revision = 6,
                data = root.GetProperty("data").GetString(),
                scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
            }));
            return true;
        });
        completeBegin = result => begin.SetResult(result);
        context.Services.AddSingleton<EquipmentInsService>(service);
        context.Services.AddSingleton<AuthenticationStateProvider, EquipmentInsAuthentication>();
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(typeDb ?? CreateDb(
            Script.Rows(TypeRow()), Script.Rows(TypeRow()), Script.Rows(TypeRow()))));
        context.Services.AddSingleton(modelSource);
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PhoneCustomerSelectDataSource(customerDb ?? CreateDb()));
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

    private sealed class EquipmentInsAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, FixturePrincipal)], "Negotiate"))));
    }

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
        public bool AllowCreate { get; set; } = true;
        public bool CopyAvailable { get; set; } = true;
        public string? CopyDescription { get; set; }
        public int? CopyTenantId { get; set; }
        public int? DeferredAccountingPersonId { get; set; }
        public int CreateCalls { get; private set; }
        public int IdentityChecks { get; private set; }
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
            if (!CopyAvailable) return Task.FromResult<EquipmentEntity?>(null);
            return Task.FromResult<EquipmentEntity?>(new EquipmentEntity
            {
                Id = id, EquipmentTypeId = 103, ModelId = 945, OwnerId = 1, LocationId = 1,
                Description = CopyDescription ?? $"Copy{id}",
                TenantId = CopyTenantId, IsAccounting = CopyTenantId is not null,
            });
        }

        public void WaitForCopyRequest()
        {
            for (var attempt = 0; attempt < 100 && !_copyRequested.Task.IsCompleted; attempt++)
                Thread.Sleep(10);
            Assert.True(_copyRequested.Task.IsCompleted, "Отложенный запрос копии не был начат");
        }

        public override Task<Employee?> GetCurrentUserAsync(string? principal, CancellationToken ct)
        {
            IdentityChecks++;
            var employee = new Employee { Id = 4 };
            if (AllowCreate)
                employee.SetRoleGrants([new EmployeeRoleGrant { RoleId = (int)Роли.Специалист_по_инвентаризации_оборудования }]);
            return Task.FromResult<Employee?>(employee);
        }

        public override Task<bool> CanUseAccountingAsync(Employee? employee, int? personId, CancellationToken ct)
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

        public override async Task<int> CreateAsync(EquipmentEntity equipment, string? principal, int expectedEmployeeId,
            CancellationToken ct, Func<EquipmentEntity, CancellationToken, Task>? beforeDispatch = null)
        {
            Assert.Equal(FixturePrincipal, principal);
            Assert.Equal(4, expectedEmployeeId);
            if (beforeDispatch is not null) await beforeDispatch(equipment, ct);
            CreateCalls++;
            if (SaveException is not null)
                throw SaveException;
            Created = equipment;
            return 123;
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
