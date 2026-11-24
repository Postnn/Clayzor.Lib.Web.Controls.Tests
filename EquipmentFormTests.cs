using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Clayzor.Lib.Web.DBSelect.Documents;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.DBSelect.Persons;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using MudBlazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
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

    /// <summary>Отзыв роли между загрузкой и Save показывает ошибку и сохраняет черновик.</summary>
    [Fact]
    public async Task SavePermissionRevoked_KeepsDraftWithoutSuccessCallbacks()
    {
        var fixture = CreateFixture();
        fixture.Service.SaveException = new UnauthorizedAccessException("Недостаточно прав для изменения оборудования.");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_DRAFT");

        await InvokeSave(page, false);

        page.WaitForAssertion(() => Assert.Contains("Недостаточно прав", page.Markup));
        Assert.Equal("CLAY_TEST_DRAFT", FindSn(page).GetAttribute("value"));
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

    /// <summary>Акты списания видны только без выбранного документа и выводятся текстом без ссылок.</summary>
    [Theory]
    [InlineData(0, null, false)]
    [InlineData(2, null, true)]
    [InlineData(1, 9, false)]
    public async Task PendingWriteOffActs_VisibleOnlyWithoutWriteoffDocument(
        int count,
        int? documentId,
        bool expected)
    {
        var acts = Enumerable.Range(1, count).Select(index => new DocumentLookup
        {
            Id = index, Name = $"Акт {index}", Number = index.ToString()
        }).ToList();
        var fixture = CreateFixture(writeOffActs: acts, writeOffDocumentId: documentId);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.Equal(expected, page.FindAll(".equipment-writeoff-acts").Count > 0);
        if (expected)
        {
            Assert.Contains("Акт 1", page.Markup);
            Assert.Contains("Акт 2", page.Markup);
        }
        Assert.Empty(page.FindAll(".equipment-writeoff-acts a"));
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
        Assert.Empty(page.FindAll("#equipment-phones"));
        await InvokeHandler(page, "OnPhoneToAddChangedAsync", 11);
        page.Render();
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

    /// <summary>После ошибки АТС повторный Save выполняет только синхронизацию без повторного UPDATE.</summary>
    [Fact]
    public async Task SyncFailure_RetriesWithoutRepeatingDatabaseSave()
    {
        var fixture = CreateFixture(phones: true);
        fixture.Service.SyncException = new InvalidOperationException("АТС недоступна");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await InvokeHandler(page, "OnPhoneToAddChangedAsync", 11);

        await InvokeSave(page, false);

        Assert.Equal(1, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(1, fixture.Service.SyncCalls);
        Assert.NotNull(GetPrivate(page.Instance, "_pendingPhoneSync"));
        fixture.Service.SyncException = null;

        var apply = page.FindAll("button").Single(item => item.TextContent.Trim() == "Применить");
        Assert.False(apply.HasAttribute("disabled"));
        await apply.ClickAsync();
        page.WaitForAssertion(() => Assert.Null(GetPrivate(page.Instance, "_pendingPhoneSync")));

        Assert.Equal(1, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(2, fixture.Service.SyncCalls);
    }

    /// <summary>После частичной ошибки повтор через отрисованную кнопку выполняет только неудачную операцию.</summary>
    [Fact]
    public async Task SyncPartialFailure_RetryUsesOnlyFailedOperation()
    {
        var fixture = CreateFixture(phones: true);
        var succeeded = new PhoneSyncOperation(PhoneSyncKind.Ccm, "8");
        var failed = new PhoneSyncOperation(PhoneSyncKind.Ccm, "9");
        fixture.Service.SyncResults =
        [
            new PhoneSyncResult(succeeded, null),
            new PhoneSyncResult(failed, "sync failed"),
        ];
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await InvokeHandler(page, "OnPhoneToAddChangedAsync", 11);
        await InvokeSave(page, false);

        var pending = GetPrivate(page.Instance, "_pendingPhoneSync")!;
        var operations = (IReadOnlyList<PhoneSyncOperation>)pending.GetType()
            .GetProperty("Operations")!.GetValue(pending)!;
        Assert.Equal(failed, Assert.Single(operations));
        fixture.Service.SyncResults = [];

        var apply = page.FindAll("button").Single(item => item.TextContent.Trim() == "Применить");
        Assert.False(apply.HasAttribute("disabled"));
        await apply.ClickAsync();
        page.WaitForAssertion(() => Assert.Null(GetPrivate(page.Instance, "_pendingPhoneSync")));

        Assert.Equal(failed, Assert.Single(fixture.Service.LastSyncOperations));
        Assert.Equal(1, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(2, fixture.Service.SyncCalls);
    }

    /// <summary>Отмена pending предупреждает о сохранённом SQL и при отказе не закрывает форму.</summary>
    [Fact]
    public async Task CancelPendingSync_ShowsWarningAndHonoursRejection()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        await CreatePendingSync(fixture, page);

        page.FindAll("button").Single(item => item.TextContent.Trim() == "Отмена").Click();
        provider.WaitForAssertion(() => Assert.Contains("При закрытии повтор из этой формы будет потерян", provider.Markup));
        await provider.FindAll("button").Single(item => item.TextContent.Trim() == "Нет").ClickAsync();
        provider.WaitForAssertion(() => Assert.DoesNotContain("Закрытие формы", provider.Markup));

        Assert.NotNull(GetPrivate(page.Instance, "_pendingPhoneSync"));
        Assert.DoesNotContain(context.JSInterop.Invocations.ToList(),
            invocation => invocation.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Подтверждение закрытия pending выполняется через реальные Отмена и Да.</summary>
    [Fact]
    public async Task CancelPendingSync_ConfirmationClosesForm()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        await CreatePendingSync(fixture, page);

        page.FindAll("button").Single(item => item.TextContent.Trim() == "Отмена").Click();
        provider.WaitForAssertion(() => Assert.Contains("При закрытии повтор из этой формы будет потерян", provider.Markup));
        await provider.FindAll("button").Single(item => item.TextContent.Trim() == "Да").ClickAsync();

        page.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations,
            invocation => invocation.Identifier == "clayModelEquipmentForm.close"));
    }

    /// <summary>Refresh после подтверждения перезагружает запись и сохраняет pending для повтора.</summary>
    [Fact]
    public async Task RefreshPendingSync_PreservesRetryInCurrentCircuit()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        await CreatePendingSync(fixture, page);
        var requestCount = fixture.Service.Requests.Count;

        page.FindAll("button").Single(item => item.TextContent.Trim() == "Обновить").Click();
        provider.WaitForAssertion(() => Assert.Contains("Обновление записи сохранит ожидающую синхронизацию", provider.Markup));
        await provider.FindAll("button").Single(item => item.TextContent.Trim() == "Да").ClickAsync();

        page.WaitForAssertion(() => Assert.Equal(requestCount + 1, fixture.Service.Requests.Count));
        Assert.NotNull(GetPrivate(page.Instance, "_pendingPhoneSync"));
        Assert.Equal(1, fixture.Service.UpdateWithPhonesCalls);
    }

    /// <summary>Переход по id сначала предупреждает, а после подтверждения не переносит pending на другую запись.</summary>
    [Fact]
    public async Task NavigationPendingSync_ConfirmsAndDoesNotCarryPendingToNewId()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        await CreatePendingSync(fixture, page);

        var rejectedChange = new LocationChangingContext { TargetLocation = "http://localhost/Equipment/EquipmentForm?id=8" };
        var rejectedTask = InvokeHandler(page, "BeforeInternalNavigationAsync", rejectedChange);
        provider.WaitForAssertion(() => Assert.Contains("При закрытии повтор из этой формы будет потерян", provider.Markup));
        await provider.FindAll("button").Single(item => item.TextContent.Trim() == "Нет").ClickAsync();
        await rejectedTask;
        WaitForSn(page, "CLAY_TEST_7");
        Assert.NotNull(GetPrivate(page.Instance, "_pendingPhoneSync"));

        var confirmedChange = new LocationChangingContext { TargetLocation = "http://localhost/Equipment/EquipmentForm?id=8" };
        var confirmedTask = InvokeHandler(page, "BeforeInternalNavigationAsync", confirmedChange);
        provider.WaitForAssertion(() => Assert.Contains("При закрытии повтор из этой формы будет потерян", provider.Markup));
        await provider.FindAll("button").Single(item => item.TextContent.Trim() == "Да").ClickAsync();
        await confirmedTask;
        await page.InvokeAsync(() => fixture.Navigation.NavigateTo(confirmedChange.TargetLocation));

        WaitForSn(page, "CLAY_TEST_8");
        Assert.Null(GetPrivate(page.Instance, "_pendingPhoneSync"));
    }

    /// <summary>Ошибка первичного SELECT или batch секций показывает Retry и восстанавливается без разрыва circuit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadSqlFailure_ShowsRetryAndRecovers(bool batchError)
    {
        var fixture = CreateFixture();
        var exception = SqlExceptionFactory.Create(53, "connection error");
        if (batchError) fixture.Service.DetailsException = exception;
        else fixture.Service.LoadException = exception;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();

        page.WaitForAssertion(() => Assert.Contains("Не удалось загрузить оборудование", page.Markup));
        Assert.DoesNotContain("Оборудование с указанным идентификатором не найдено", page.Markup);
        var retry = page.FindAll(".mud-alert button").Single(item => item.TextContent.Trim() == "Обновить");
        Assert.False(retry.HasAttribute("disabled"));

        await retry.ClickAsync();

        WaitForSn(page, "CLAY_TEST_7");
        Assert.DoesNotContain("Не удалось загрузить оборудование", page.Markup);
    }

    /// <summary>Добавление номера обновляет все прикладные фильтры и исключает его из следующего поиска.</summary>
    [Fact]
    public async Task PhoneFilters_FollowCurrentStateAndAddedNumbers()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).EmployeeId = 4;
        SetPrivate(page.Instance, "_phoneToAdd", 11);

        await InvokeHandler(page, "AddPhoneAsync");

        var source = context.Services.GetRequiredService<PhoneNumberSelectDataSource>();
        Assert.Equal(55, source.LocationId);
        Assert.Equal(4, source.EmployeeId);
        Assert.Equal(103, source.EquipmentTypeId);
        Assert.Equal(7, source.EquipmentId);
        Assert.Equal(11, Assert.Single(source.ExcludedPhoneIds));
    }

    /// <summary>Смена владельца меняет фильтры и права, но не уничтожает документы и поставщика.</summary>
    [Fact]
    public async Task OwnerChange_PreservesDocumentsAndShipper()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).DocumentReceiptOuId = 10;
        CurrentModel(page.Instance).DocumentWriteOffId = 11;
        CurrentModel(page.Instance).ShipperId = 12;

        await InvokeHandler(page, "OnOwnerChangedAsync", 2);

        Assert.Equal(10, CurrentModel(page.Instance).DocumentReceiptOuId);
        Assert.Equal(11, CurrentModel(page.Instance).DocumentWriteOffId);
        Assert.Equal(12, CurrentModel(page.Instance).ShipperId);
        Assert.Equal(2, context.Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt").PersonId);
        Assert.Equal(2, context.Services.GetRequiredKeyedService<DocumentSelectDataSource>("writeoff").PersonId);
        Assert.True(((ClaySelectOptions)GetPrivate(page.Instance, "_tenantOptions")!).ReadOnly);

        await InvokeHandler(page, "OnOwnerChangedAsync", (int?)null);
        Assert.Null(context.Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt").PersonId);
        Assert.Null(context.Services.GetRequiredKeyedService<DocumentSelectDataSource>("writeoff").PersonId);
    }

    /// <summary>Форма использует всю ширину, legacy-порядок команд и отображает ФИО изменившего.</summary>
    [Fact]
    public async Task Layout_UsesFullWidthLegacyOrderAndChangedEmployeeName()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var style = page.Find(".equipment-form").GetAttribute("style") ?? string.Empty;
        Assert.Contains("max-width:100%", style.Replace(" ", string.Empty));
        var commands = page.FindAll("button").Select(button => button.TextContent.Trim()).Take(5).ToArray();
        Assert.Equal(new[] { "OK", "Применить", "Копировать", "Обновить", "Отмена" }, commands);
        var pairs = page.FindAll(".equipment-form-pair");
        Assert.Contains("equipment-form-writeoff-row", pairs[0].ClassList);
        Assert.Contains("SN:", pairs[0].TextContent);
        Assert.Contains("Списано:", pairs[0].TextContent);
        Assert.Contains("Тип:", pairs[1].TextContent);
        Assert.Contains("Модель:", pairs[1].TextContent);
        Assert.Equal("Тестов А.", page.Find(".clay-changed-user").TextContent.Trim());
        Assert.Equal("http://localhost/user?id=4", page.Find(".clay-changed-user").GetAttribute("href"));
        Assert.Contains(page.FindAll("input"), input => input.GetAttribute("value") == "001122334455");
        Assert.DoesNotContain(page.FindAll("input"), input => input.GetAttribute("value")?.StartsWith("_model.MacAddress") == true);
    }

    /// <summary>Сетевая история загружается для оборудования с сетевым именем.</summary>
    [Fact]
    public async Task NetworkHistory_LoadsForNetworkEquipment()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.Contains(page.FindAll("button"), button => button.TextContent.Trim() == "История имён");
        var history = (IReadOnlyList<EquipmentNetworkNameHistory>)GetPrivate(page.Instance, "_networkNameHistory")!;
        Assert.Equal("old-host", Assert.Single(history).NetworkName);
    }

    /// <summary>Обратный поиск показывает порт локальной сети и открывает его через clayOpen.</summary>
    [Fact]
    public async Task NetworkPortLink_OpensLegacyNetForm()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var link = page.Find("[data-testid='equipment-network-ports'] a");
        Assert.Equal("clay-test-net_12", link.TextContent.Trim());
        Assert.Equal("http://localhost/Net/Net?id=11", link.GetAttribute("href"));
        await link.ClickAsync();

        var open = Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "clayOpen");
        Assert.Equal("http://localhost/Net/Net?id=11", open.Arguments[0]);
        Assert.Equal("net-form", open.Arguments[1]);
    }

    /// <summary>Изменил в секциях — активная ссылка на сотрудника через clayOpen; неактивные заголовки не имеют sort/drag состояния.</summary>
    [Fact]
    public async Task DetailGridEmployeeLink_OpensUserFormAndHeadersArePassive()
    {
        var fixture = CreateFixture();
        fixture.Service.StoredLocations.Add(new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 55, LocationName = "На руках",
            ChangedById = 4, ChangedByName = "Тестов А.", FromUtc = DateTime.Today.AddDays(-1),
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var link = page.Find("#equipment-locations tbody a");
        Assert.Equal("http://localhost/user?id=4", link.GetAttribute("href"));

        await page.InvokeAsync(() => page.Find("#equipment-locations tbody a").Click());

        var invocation = context.JSInterop.Invocations.Single(call => call.Identifier == "clayOpen");
        Assert.Equal("http://localhost/user?id=4", invocation.Arguments[0]);
        Assert.Equal("user-form", invocation.Arguments[1]);
        Assert.All(page.FindAll("#equipment-locations .clay-grid-column-drag-handle"), handle =>
        {
            Assert.Equal("false", handle.GetAttribute("draggable"));
            Assert.DoesNotContain("clay-grid-column-sortable", handle.ClassList);
        });
    }

    /// <summary>Все характеристики выбранной модели выводятся в разметке формы.</summary>
    [Fact]
    public async Task ModelCharacteristics_RenderCompleteLegacyMetadata()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.Contains("PN-945", page.Markup);
        Assert.Contains("Мультимедиа", page.Markup);
        Assert.Contains("Диагональ: 24,0", page.Markup);
        Assert.Contains("CPU-X", page.Markup);
        Assert.Contains("Ethernet", page.Markup);
        Assert.Contains("DDR4", page.Markup);
        Assert.Contains("кол-во банков: 2", page.Markup);
        Assert.Contains("Driver-X", page.Markup);
        Assert.Contains("100000 часов", page.Markup);
        Assert.Contains("Model notes", page.Markup);
    }

    /// <summary>Очистка модели скрывает её характеристики, а восстановление ID использует сохранённый объект.</summary>
    [Fact]
    public async Task ModelClearAndRestore_DoesNotKeepStaleVisibleState()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var backup = GetPrivate(page.Instance, "_selectedModel");

        await InvokeHandler(page, "OnModelIdChanged", (int?)null);
        Assert.False(BoolProperty(page.Instance, "HasSelectedModel"));
        Assert.Equal(0, IntProperty(page.Instance, "MacCount"));
        Assert.Same(backup, GetPrivate(page.Instance, "_selectedModel"));

        await InvokeHandler(page, "OnModelIdChanged", 945);
        Assert.True(BoolProperty(page.Instance, "HasSelectedModel"));
        Assert.Same(backup, GetPrivate(page.Instance, "_selectedModel"));
    }

    /// <summary>Подтверждённая смена типа сбрасывает модель и её устаревшие признаки.</summary>
    [Fact]
    public async Task TypeChange_ClearsModelAndDependentSelection()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "OnTypeIdChanged", 30);

        Assert.Equal(30, CurrentModel(page.Instance).EquipmentTypeId);
        Assert.Null(CurrentModel(page.Instance).ModelId);
        Assert.Null(GetPrivate(page.Instance, "_selectedType"));
        Assert.Null(GetPrivate(page.Instance, "_selectedModel"));
        Assert.Equal(30, context.Services.GetRequiredService<EquipmentModelSelectDataSource>().Filter.EquipmentTypeId);
    }

    /// <summary>Выбор модели без типа автоматически восстанавливает тип и загружает телефонные связи.</summary>
    [Fact]
    public async Task ModelWithoutType_SelectsTypeAndLoadsPhones()
    {
        var fixture = CreateFixture(phones: true, phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).EquipmentTypeId = null;
        CurrentModel(page.Instance).ModelId = null;
        SetPrivate(page.Instance, "_selectedType", null);
        SetPrivate(page.Instance, "_selectedModel", null);
        var reads = fixture.Service.GetPhonesCalls;

        await InvokeHandler(page, "OnModelChangedAsync", new EquipmentModel
        {
            Id = 945, EquipmentTypeId = 30, Name = "Phone", NetworkInterfaces = 1,
        });

        Assert.Equal(30, CurrentModel(page.Instance).EquipmentTypeId);
        Assert.Equal(30, ((EquipmentType)GetPrivate(page.Instance, "_selectedType")!).Id);
        Assert.Equal(reads + 1, fixture.Service.GetPhonesCalls);
    }

    /// <summary>Подтверждённая очистка типа удаляет старые объекты и фильтр поиска модели.</summary>
    [Fact]
    public async Task TypeClearConfirmed_RemovesTypeModelAndSearchFilter()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "OnTypeClearConfirmed");

        Assert.Null(CurrentModel(page.Instance).EquipmentTypeId);
        Assert.Null(CurrentModel(page.Instance).ModelId);
        Assert.Null(GetPrivate(page.Instance, "_selectedType"));
        Assert.Null(GetPrivate(page.Instance, "_selectedModel"));
        Assert.Null(context.Services.GetRequiredService<EquipmentModelSelectDataSource>().Filter.EquipmentTypeId);
        Assert.Null(context.Services.GetRequiredService<PhoneNumberSelectDataSource>().EquipmentTypeId);
    }

    /// <summary>Смена типа не очищает несохранённые сетевые и телефонные значения.</summary>
    [Fact]
    public async Task TypeSwitch_PreservesDirtyFeatureValues()
    {
        var fixture = CreateFixture(phones: true, phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).NetworkName = "draft-host";
        CurrentModel(page.Instance).MacAddress = "001122334455";
        CurrentModel(page.Instance).MacAddress2 = "a0b1c2d3e4f5";
        CurrentModel(page.Instance).PhonePoolId = 12;
        CurrentModel(page.Instance).CameraIsConnected = 1;

        await InvokeHandler(page, "OnTypeChanged", new EquipmentType { Id = 104, Name = "Other" });

        Assert.Equal("draft-host", CurrentModel(page.Instance).NetworkName);
        Assert.Equal("001122334455", CurrentModel(page.Instance).MacAddress);
        Assert.Equal("a0b1c2d3e4f5", CurrentModel(page.Instance).MacAddress2);
        Assert.Equal(12, CurrentModel(page.Instance).PhonePoolId);
        Assert.Equal(1, CurrentModel(page.Instance).CameraIsConnected);
    }

    /// <summary>Переход A→B→A сохраняет добавленный телефон и исходный diff baseline.</summary>
    [Fact]
    public async Task TypeSwitchAwayAndBack_PreservesUncommittedPhoneDiff()
    {
        var fixture = CreateFixture(phones: true, phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var originalType = (EquipmentType)GetPrivate(page.Instance, "_selectedType")!;
        var phones = (List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!;
        phones.Add(new EquipmentPhoneLink { PhoneId = 99, EquipmentId = 7, Description = "draft" });
        var reads = fixture.Service.GetPhonesCalls;

        await InvokeHandler(page, "OnTypeChanged", new EquipmentType { Id = 31, Name = "Other", HasPhoneNumber = 1 });
        await InvokeHandler(page, "OnTypeChanged", originalType);

        Assert.Equal(reads, fixture.Service.GetPhonesCalls);
        Assert.Contains(phones, item => item.PhoneId == 99);
        Assert.Empty((List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_originalPhones")!);
        Assert.True(BoolProperty(page.Instance, "PhonesModified"));
    }

    /// <summary>Существующие компьютерные данные остаются видимыми read-only при несовместимом черновике типа.</summary>
    [Fact]
    public async Task DraftTypeChange_KeepsPersistedComputerDataVisibleReadOnly()
    {
        var fixture = CreateFixture(computer: true);
        fixture.Service.StoredComputers.Add(new EquipmentComputerSnapshot
        {
            EquipmentId = 7, Date = new DateTime(2026, 11, 18), RamMb = 1024
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "OnTypeChanged", new EquipmentType { Id = 104, Name = "Other" });

        Assert.True(BoolProperty(page.Instance, "ShowComputer"));
        Assert.False(BoolProperty(page.Instance, "CanEditComputer"));
        Assert.Single((List<EquipmentComputerSnapshot>)GetPrivate(page.Instance, "_computerHistory")!);
        var options = (ClayGridOptions)GetPrivate(page.Instance, "_computerGridOptions")!;
        Assert.False(options.ShowAddButton);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#equipment-computer tbody tr")));
    }

    /// <summary>Число MAC-полей соответствует Net=0/1/2/3+ и отсутствию модели.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    public async Task MacCount_FollowsModelNetworkInterfaces(int interfaces, int expected)
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        SetPrivate(page.Instance, "_selectedModel", new EquipmentModel { Id = 945, NetworkInterfaces = interfaces });
        Assert.Equal(expected, IntProperty(page.Instance, "MacCount"));
    }

    /// <summary>Камера и пул относятся только к IP-телефону, а не к monitor-признаку.</summary>
    [Fact]
    public async Task CameraAndPhonePool_RequireIpPhoneType()
    {
        var fixture = CreateFixture(phones: true, phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        Assert.True(BoolProperty(page.Instance, "ShowCamera"));
        Assert.True(BoolProperty(page.Instance, "ShowPhonePool"));

        var type = (EquipmentType)GetPrivate(page.Instance, "_selectedType")!;
        type.Id = 104;
        type.HasMonitorCharacteristics = 1;
        Assert.False(BoolProperty(page.Instance, "ShowCamera"));
        Assert.False(BoolProperty(page.Instance, "ShowPhonePool"));
        Assert.True(BoolProperty(page.Instance, "ShowPhones"));
    }

    /// <summary>Открытое из формы перемещение «На руках» требует сотрудника и сохраняет обе связи.</summary>
    [Fact]
    public async Task OnHandsLocationFromForm_RequiresAndSavesResponsible()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenLocationDialogAsync", (object?)null);
        await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<EquipmentLocationDialog>()));
        var dialog = provider.FindComponent<EquipmentLocationDialog>();
        Assert.True(dialog.Instance.RequireResponsible);
        dialog.Instance.LocationId = 55;
        dialog.Instance.FromDate = DateTime.Today;
        dialog.Instance.FromTime = DateTime.Now.AddMinutes(-1).TimeOfDay;

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.Empty(fixture.Service.LocationSaves);
        Assert.Contains("укажите ответственного сотрудника", dialog.Markup);

        dialog.Instance.EmployeeId = 4;
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await pending;

        var save = Assert.Single(fixture.Service.LocationSaves);
        Assert.Equal(55, save.Location.LocationId);
        Assert.Equal(4, save.Responsible?.EmployeeId);
    }

    /// <summary>Refresh компьютера переводит новый UTC ChangedAt в клиентское время.</summary>
    [Fact]
    public async Task ComputerRefresh_LocalizesNewChangedAt()
    {
        var fixture = CreateFixture(computer: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var changedAt = new DateTime(2026, 11, 22, 18, 47, 6, DateTimeKind.Unspecified);
        fixture.Service.StoredComputers.Add(new EquipmentComputerSnapshot
        {
            EquipmentId = 7, Date = DateTime.Today, ChangedAt = changedAt,
        });

        await InvokeHandler(page, "RefreshComputerAsync");

        Assert.Equal(changedAt.AddMinutes(180),
            ((Dictionary<DateTime, DateTime>)GetPrivate(page.Instance, "_localDates")!)[changedAt]);
    }

    /// <summary>Реальный OK компьютера ждёт запись и не закрывает редактор при отложенной ошибке.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputerSave_ActualCallbackAwaitsWriteAndObservesFault(bool fault)
    {
        var fixture = CreateFixture(computer: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenComputerDialogAsync");
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dialog = provider.FindComponent<EquipmentComputerDialog>();
        var initialRefreshes = fixture.Service.ComputerRefreshCalls;

        var click = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await fixture.Service.ComputerSaveRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(PrivateBool(page.Instance, "_saving"));
        Assert.Equal(initialRefreshes, fixture.Service.ComputerRefreshCalls);
        Assert.False(click.IsCompleted);

        if (fault)
            fixture.Service.ComputerSaveCompletion.SetException(new InvalidOperationException("Отложенная ошибка записи"));
        else
            fixture.Service.ComputerSaveCompletion.SetResult();
        await click;

        Assert.Equal(1, fixture.Service.ComputerSaveCalls);
        if (fault)
        {
            Assert.Contains("Отложенная ошибка записи", dialog.Markup);
            Assert.Equal(initialRefreshes, fixture.Service.ComputerRefreshCalls);
            await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        }
        else
        {
            Assert.Equal(initialRefreshes + 1, fixture.Service.ComputerRefreshCalls);
        }
        await pending;
    }

    /// <summary>Отложенное открытие Computer не связывает данные A с загруженной за время ожидания карточкой B.</summary>
    [Fact]
    public async Task ComputerOpen_DeferredTimeAfterNavigation_DoesNotOpenOrSave()
    {
        var fixture = CreateFixture(computer: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        fixture.Js.DeferredIdentifier = "clayGridTimeZone.getOffsetMinutes";

        var pending = InvokeHandler(page, "OpenComputerDialogAsync");
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Js.DeferredIdentifier = null;
        typeof(EquipmentForm).GetProperty(nameof(EquipmentForm.Id))!.SetValue(page.Instance, 8);
        await InvokeHandler(page, "LoadCurrentAsync", 8);
        WaitForSn(page, "CLAY_TEST_8");
        fixture.Js.Completion.SetResult();
        await pending;

        Assert.Empty(provider.FindComponents<EquipmentComputerDialog>());
        Assert.Equal(0, fixture.Service.ComputerSaveCalls);
    }

    /// <summary>Смена карточки после отложенного чтения времени запрещает dispatch Computer Save.</summary>
    [Fact]
    public async Task ComputerSave_DeferredTimeAfterNavigation_DoesNotDispatch()
    {
        var fixture = CreateFixture(computer: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenComputerDialogAsync");
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dialog = provider.FindComponent<EquipmentComputerDialog>();
        fixture.Js.DeferredIdentifier = "clayGridTimeZone.getOffsetMinutes";

        var click = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Js.DeferredIdentifier = null;
        typeof(EquipmentForm).GetProperty(nameof(EquipmentForm.Id))!.SetValue(page.Instance, 8);
        await InvokeHandler(page, "LoadCurrentAsync", 8);
        WaitForSn(page, "CLAY_TEST_8");
        fixture.Js.Completion.SetResult();
        await click;

        Assert.Equal(0, fixture.Service.ComputerSaveCalls);
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await pending;
    }

    /// <summary>При редактировании неизменяемые расположение, дата и время визуально и функционально отключены.</summary>
    [Fact]
    public async Task LocationEdit_DisablesFixedPeriodFields()
    {
        var fixture = CreateFixture();
        var existing = new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 55, FromUtc = DateTime.Today.AddDays(-1), Notes = "note",
        };
        fixture.Service.StoredLocations.Add(existing);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenLocationDialogAsync", existing);
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dialog = provider.FindComponent<EquipmentLocationDialog>();

        Assert.True(dialog.FindComponents<MudDatePicker>()[0].Instance.Disabled);
        Assert.True(Assert.Single(dialog.FindComponents<MudTimePicker>()).Instance.Disabled);
        Assert.False(dialog.FindComponents<MudDatePicker>()[1].Instance.Disabled);
        var expectedLocal = existing.FromUtc.AddMinutes(180);
        Assert.Equal(expectedLocal.Date, dialog.Instance.FromDate);
        Assert.Equal(expectedLocal.TimeOfDay, dialog.Instance.FromTime);
        var select = dialog.FindComponent<ClaySelect<LocationLookup, int?>>();
        Assert.True(select.Instance.Options.ReadOnly);

        await page.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await pending;
    }

    /// <summary>Child refresh сохраняет локальный SN и исходный token при чужом изменении того же поля.</summary>
    [Fact]
    public async Task ChildRefresh_ConflictingMainChange_DoesNotIssueFreshTokenOrOverwrite()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_LOCAL");
        var original = OriginalEquipment(page);
        var fresh = OriginalEquipment(page);
        fresh.SerialNumber = "CLAY_TEST_FOREIGN";
        fresh.Description = "Независимое изменение";
        fresh.ChangedAtUtc = fresh.ChangedAtUtc.AddSeconds(1);
        fixture.Service.NextModel = fresh;

        await InvokeHandler(page, "RefreshMainAfterChildWriteAsync", original);
        await InvokeSave(page, false);

        Assert.Equal("CLAY_TEST_LOCAL", CurrentModel(page.Instance).SerialNumber);
        Assert.Equal("Независимое изменение", CurrentModel(page.Instance).Description);
        Assert.Equal(original.ChangedAtUtc, CurrentModel(page.Instance).ChangedAtUtc);
        Assert.Equal("CLAY_TEST_7", OriginalEquipment(page).SerialNumber);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Contains("SerialNumber", GetPrivate(page.Instance, "_mainConflictMessage")!.ToString());
    }

    /// <summary>Child refresh объединяет независимые поля и принимает свежий token без ложного конфликта.</summary>
    [Fact]
    public async Task ChildRefresh_IndependentChanges_MergesWithoutConflict()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_LOCAL");
        var original = OriginalEquipment(page);
        var fresh = OriginalEquipment(page);
        fresh.Description = "Независимое изменение";
        fresh.ChangedAtUtc = fresh.ChangedAtUtc.AddSeconds(1);
        fixture.Service.NextModel = fresh;

        await InvokeHandler(page, "RefreshMainAfterChildWriteAsync", original);

        Assert.Equal("CLAY_TEST_LOCAL", CurrentModel(page.Instance).SerialNumber);
        Assert.Equal("Независимое изменение", CurrentModel(page.Instance).Description);
        Assert.Equal(fresh.ChangedAtUtc, CurrentModel(page.Instance).ChangedAtUtc);
        Assert.Null(GetPrivate(page.Instance, "_mainConflictMessage"));
        Assert.True(ValidateBusinessRules(page.Instance));
    }

    /// <summary>Собственные изменения расположения и ответственного не становятся ложным конфликтом main.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(null)]
    public async Task ChildRefresh_DerivedLocationAndResponsible_MergeWithoutConflict(int? freshEmployeeId)
    {
        var fixture = CreateFixture();
        fixture.Service.EmployeeId = 4;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_LOCAL");
        var original = OriginalEquipment(page);
        var fresh = OriginalEquipment(page);
        fresh.LocationId = 66;
        fresh.EmployeeId = freshEmployeeId;
        fresh.ChangedAtUtc = fresh.ChangedAtUtc.AddSeconds(1);
        fixture.Service.NextModel = fresh;

        await InvokeHandler(page, "RefreshMainAfterChildWriteAsync", original);

        Assert.Equal(66, CurrentModel(page.Instance).LocationId);
        Assert.Equal(freshEmployeeId, CurrentModel(page.Instance).EmployeeId);
        Assert.Equal("CLAY_TEST_LOCAL", CurrentModel(page.Instance).SerialNumber);
        Assert.Null(GetPrivate(page.Instance, "_mainConflictMessage"));
        Assert.True(ValidateBusinessRules(page.Instance));
    }

    /// <summary>Durable restore объединяет локальный ввод с независимым свежим изменением.</summary>
    [Fact]
    public async Task RestoreDraft_IndependentFreshChange_IsMerged()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var originalSnapshot = (string)GetPrivate(page.Instance, "_originalSnapshot")!;
        var local = OriginalEquipment(page);
        local.SerialNumber = "CLAY_TEST_LOCAL";
        CurrentModel(page.Instance).Description = "Независимое свежее изменение";
        CurrentModel(page.Instance).ChangedAtUtc = CurrentModel(page.Instance).ChangedAtUtc.AddSeconds(1);
        var expectedToken = CurrentModel(page.Instance).ChangedAtUtc;
        var json = DraftJson(local, originalSnapshot);

        await InvokeHandler(page, "RestoreDraftAsync", json);

        Assert.Equal("CLAY_TEST_LOCAL", CurrentModel(page.Instance).SerialNumber);
        Assert.Equal("Независимое свежее изменение", CurrentModel(page.Instance).Description);
        Assert.Equal(expectedToken, CurrentModel(page.Instance).ChangedAtUtc);
        Assert.Null(GetPrivate(page.Instance, "_mainConflictMessage"));
    }

    /// <summary>Durable restore сохраняет конфликт и позволяет заменить только конфликтные поля.</summary>
    [Fact]
    public async Task RestoreDraft_SameFieldConflict_PreservesBothAndResolvesSelectively()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var originalSnapshot = (string)GetPrivate(page.Instance, "_originalSnapshot")!;
        var originalToken = CurrentModel(page.Instance).ChangedAtUtc;
        var local = OriginalEquipment(page);
        local.SerialNumber = "CLAY_TEST_LOCAL";
        local.Description = "Локальное описание";
        CurrentModel(page.Instance).SerialNumber = "CLAY_TEST_FRESH";
        CurrentModel(page.Instance).ChangedAtUtc = CurrentModel(page.Instance).ChangedAtUtc.AddSeconds(1);
        var json = DraftJson(local, originalSnapshot);

        await InvokeHandler(page, "RestoreDraftAsync", json);

        Assert.Equal("CLAY_TEST_LOCAL", CurrentModel(page.Instance).SerialNumber);
        Assert.Equal(originalToken, CurrentModel(page.Instance).ChangedAtUtc);
        Assert.Contains("SerialNumber", GetPrivate(page.Instance, "_mainConflictMessage")!.ToString());
        await InvokeHandler(page, "ResolveMainConflict", false);
        Assert.Equal("CLAY_TEST_FRESH", CurrentModel(page.Instance).SerialNumber);
        Assert.Equal("Локальное описание", CurrentModel(page.Instance).Description);
        Assert.Null(GetPrivate(page.Instance, "_mainConflictMessage"));
    }

    /// <summary>Read-only сверка подтверждённого результата завершает Unknown без повторной записи.</summary>
    [Fact]
    public async Task ReconcileDraftOperation_MatchingSnapshot_CommitsWithoutWrites()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetDraftMainOperation(page, (string)GetPrivate(page.Instance, "_originalSnapshot")!);

        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.False((bool)GetPrivate(page.Instance, "_draftOperationUnresolved")!);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Equal(0, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(0, fixture.Service.SyncCalls);
    }

    /// <summary>Read-only сверка расхождения сохраняет Unknown и не разрешает повтор.</summary>
    [Fact]
    public async Task ReconcileDraftOperation_DifferentSnapshot_KeepsUnknownWithoutWrites()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetDraftMainOperation(page, (string)GetPrivate(page.Instance, "_originalSnapshot")!);
        var fresh = OriginalEquipment(page);
        fresh.SerialNumber = "CLAY_TEST_FOREIGN";
        fixture.Service.NextModel = fresh;

        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.True((bool)GetPrivate(page.Instance, "_draftOperationUnresolved")!);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Equal(0, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(0, fixture.Service.SyncCalls);
    }

    /// <summary>Поздний ответ и ошибка владельца A не перекрывают успешно выбранного B.</summary>
    [Fact]
    public async Task OwnerChanged_LateFaultFromA_DoesNotOverwriteB()
    {
        var fixture = CreateFixture();
        var a = new TaskCompletionSource<Employee?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<Employee?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.OwnerReadCompletions.Enqueue(a);
        fixture.Service.OwnerReadCompletions.Enqueue(b);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        fixture.Service.DeferOwnerRead = true;

        var pendingA = InvokeHandler(page, "OnOwnerChangedAsync", 2);
        var pendingB = InvokeHandler(page, "OnOwnerChangedAsync", 3);
        b.SetResult(null);
        await pendingB;
        a.SetException(new InvalidOperationException("late read failure"));
        await pendingA;

        Assert.Equal(3, CurrentModel(page.Instance).OwnerId);
        Assert.Equal(3, GetPrivate(page.Instance, "_confirmedOwnerId"));
        Assert.Null(GetPrivate(page.Instance, "_validationMessage"));
    }

    /// <summary>Ошибка старого чтения SIM-фильтра не перекрывает успешное новое состояние.</summary>
    [Fact]
    public async Task SimFilters_LateFaultFromA_DoesNotOverwriteSuccessfulB()
    {
        var fixture = CreateFixture();
        var a = new TaskCompletionSource<IReadOnlyList<EquipmentSimService>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<IReadOnlyList<EquipmentSimService>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.SimServiceReadCompletions.Enqueue(a);
        fixture.Service.SimServiceReadCompletions.Enqueue(b);
        fixture.Service.DeferSimServiceRead = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var pendingA = InvokeHandler(page, "OnSimServiceFiltersChangedAsync");
        var pendingB = InvokeHandler(page, "OnSimServiceFiltersChangedAsync");
        b.SetResult([]);
        await pendingB;
        a.SetException(new InvalidOperationException("late filter failure"));
        await pendingA;

        Assert.Null(GetPrivate(page.Instance, "_validationMessage"));
    }

    /// <summary>Нетронутый UPS test сохраняет исходный UTC instant с миллисекундами.</summary>
    [Fact]
    public async Task UpsSave_UnchangedTimestamp_PreservesFractionalRawInstant()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var instant = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var original = new UpsTest { Id = 5, UpsEquipmentId = 10, TestedAt = instant, BatteryTime = 10 };
        var local = CloneUpsTestForTest(original, instant.AddMinutes(180));

        await InvokeHandler(page, "SaveUpsDraftAsync",
            new UpsServerLink { UpsEquipmentId = 10 },
            Array.Empty<UpsServerLink>(),
            new[] { original },
            new EquipmentUpsDialogResult([], [local]));

        Assert.Equal(instant, Assert.Single(fixture.Service.LastUpsCurrentTests!).TestedAt);
    }

    /// <summary>Диалог прежней записи после смены id не выполняет SQL для новой записи.</summary>
    [Fact]
    public async Task AccountDialogFromPreviousId_DoesNotSaveIntoNewEquipment()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenAccountDialogAsync", (object?)null);
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var editor = provider.FindComponent<EquipmentAccountDialog>();
        var editorInstance = editor.Instance;
        editorInstance.Login = "account-from-a";
        var apply = typeof(EquipmentAccountDialog).GetMethod("Apply",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        await page.InvokeAsync(() => (Task)apply.Invoke(editorInstance, null)!);

        Assert.Equal(0, fixture.Service.AddAccountCalls);
        Assert.Equal("Контекст оборудования изменился. Откройте форму повторно.",
            typeof(EquipmentEditorDialogBase<EquipmentAccountDialogResult>).GetField("_error",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editorInstance));
        dialog.Close(DialogResult.Cancel());
        await pending;
    }

    /// <summary>Подтверждение удаления SIM прежней записи аннулируется после смены id.</summary>
    [Fact]
    public async Task DeleteSimConfirmationFromPreviousId_DoesNotDeleteNewEquipment()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canEditSim", true);
        SetPrivate(page.Instance, "_selectedType", new EquipmentType { Id = 27, HasSimCharacteristics = 1 });
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "DeleteSimAsync");
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        dialog.Close(DialogResult.Ok(true));
        await pending;

        Assert.Equal(0, fixture.Service.DeleteSimCalls);
    }

    /// <summary>Поздний JS-ответ загрузки A не меняет поля полностью загруженной записи B.</summary>
    [Fact]
    public async Task LateTimeZoneResultFromPreviousLoad_DoesNotChangeCurrentEquipment()
    {
        var fixture = CreateFixture();
        var dateA = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var dateB = new DateTime(2026, 7, 20, 15, 30, 0, DateTimeKind.Utc);
        var sectionDateA = new DateTime(2026, 2, 11, 9, 45, 0, DateTimeKind.Utc);
        fixture.Service.WriteOffDates[7] = dateA;
        fixture.Service.WriteOffDates[8] = dateB;
        fixture.Service.StoredLocations.Add(new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 55, FromUtc = sectionDateA,
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        fixture.Js.DeferredIdentifier = "clayGridTimeZone.getOffsetMinutes";
        fixture.Js.DeferredArgument = sectionDateA.ToString("O");
        var loadA = InvokeHandler(page, "LoadCurrentAsync", 7);
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        fixture.Js.Completion.SetResult();
        await loadA;

        Assert.Equal(8, CurrentModel(page.Instance).Id);
        Assert.Equal(8, CurrentModel(page.Instance).TenantId);
        Assert.Equal(dateB.AddMinutes(180).Date, GetPrivate(page.Instance, "_writeOffDate"));
        var localDates = (Dictionary<DateTime, DateTime>)GetPrivate(page.Instance, "_localDates")!;
        Assert.False(localDates.ContainsKey(sectionDateA));
        Assert.False(IsModified(page.Instance));
    }

    /// <summary>Закрытие формы во время отложенного callback после SQL завершает Save без исключения.</summary>
    [Fact]
    public async Task DisposeDuringRefreshCallback_AfterSuccessfulSaveIsObserved()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_CALLBACK");
        fixture.Js.DeferredIdentifier = "clayModelEquipmentForm.refreshOpener";
        var save = InvokeSave(page, false);
        await fixture.Js.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        page.Instance.Dispose();
        fixture.Js.Completion.SetException(new OperationCanceledException());
        await save;

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.True(PrivateBool(page.Instance, "_disposed"));
    }

    /// <summary>SQL-ошибка изменения ответственного не меняет отображаемый исходный объект.</summary>
    [Fact]
    public async Task ResponsibleSqlException_KeepsDisplayedRowUnchanged()
    {
        var fixture = CreateFixture();
        var originalDate = new DateTime(2026, 11, 20);
        var responsible = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, EmployeeId = 4, FromUtc = new DateTime(2026, 11, 1),
            ReturnDate = originalDate, Notes = "old",
        };
        fixture.Service.StoredResponsibles.Add(responsible);
        fixture.Service.ResponsibleException = SqlExceptionFactory.Create(50000, "trigger");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenResponsibleDialogAsync", EquipmentResponsibleDialogMode.Edit, responsible);
        await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var editor = provider.FindComponent<EquipmentResponsibleDialog>();
        Assert.NotNull(editor.Instance.Original);
        Assert.True(editor.Instance.Original.ExpectedExisting);
        editor.Instance.ReturnDate = originalDate.AddDays(1);
        editor.Instance.Notes = "new";

        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.Equal(originalDate, responsible.ReturnDate);
        Assert.Equal("old", responsible.Notes);
        Assert.Contains("Команда «Сохранение ответственного» не выполнена", editor.Markup);
        Assert.Equal(originalDate.AddDays(1), fixture.Service.ResponsibleUpdated?.ReturnDate);
        Assert.Equal("new", editor.Instance.Notes);
    }

    /// <summary>Ошибка соединения при удалении бухучёта не удаляет строку из UI и не запускает success-refresh.</summary>
    [Fact]
    public async Task DeleteAccountingConnectivity_DoesNotRefreshOrRemoveRow()
    {
        var fixture = CreateFixture();
        fixture.Service.StoredAccounting.Add(new EquipmentAccounting { Id = 41, EquipmentId = 7, OwnerId = 1 });
        fixture.Service.DeleteAccountingException = SqlExceptionFactory.Create(53, "connection error");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "DeleteAccountingAsync", fixture.Service.StoredAccounting[0]);
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => dialog.Close(DialogResult.Ok(true)));
        await pending;

        page.WaitForAssertion(() => Assert.Contains("Результат команды «Удаление бухгалтерского учёта» неизвестен", page.Markup));
        Assert.Single(fixture.Service.StoredAccounting);
        Assert.Equal(0, fixture.Service.AccountingRefreshCalls);
    }

    /// <summary>SQL-ошибка UPDATE бухучёта сохраняет draft и не запускает success-refresh.</summary>
    [Fact]
    public async Task SaveAccountingConnectivity_KeepsDraftAndDoesNotRefresh()
    {
        var fixture = CreateFixture();
        var existing = new EquipmentAccounting
        {
            Id = 41, EquipmentId = 7, OwnerId = 1, Notes = "old",
            ChangedAt = new DateTime(2026, 11, 22, 18, 29, 44),
        };
        fixture.Service.StoredAccounting.Add(existing);
        fixture.Service.SaveAccountingException = SqlExceptionFactory.Create(53, "connection error");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_ACC_DRAFT");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenAccountingDialogAsync", existing);
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var editor = provider.FindComponent<EquipmentAccountingDialog>();
        var draft = editor.Instance.Value;
        draft.IsMaterial = true;
        draft.DateFrom = DateTime.Today;
        draft.Notes = "new draft";

        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        editor.WaitForAssertion(() => Assert.Contains("Результат команды «Сохранение бухгалтерского учёта» неизвестен", editor.Markup));
        Assert.Same(draft, fixture.Service.LastAccountingDraft);
        Assert.Equal("new draft", editor.Instance.Value.Notes);
        Assert.Equal(existing.ChangedAt, editor.Instance.Value.ChangedAt);
        Assert.True(editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        Assert.Equal(0, fixture.Service.AccountingRefreshCalls);
        Assert.Equal("CLAY_TEST_ACC_DRAFT", FindSn(page).GetAttribute("value"));
        Assert.Equal("old", existing.Notes);
    }

    /// <summary>Unknown child переживает Cancel, Refresh и повторное открытие без Discard и потери baseline.</summary>
    [Fact]
    public async Task UnknownAccounting_CancelRefreshReopen_PreservesMarkerAndDraft()
    {
        var fixture = CreateFixture();
        var existing = new EquipmentAccounting
        {
            Id = 41, EquipmentId = 7, OwnerId = 1, Notes = "old",
            ChangedAt = new DateTime(2026, 11, 22, 18, 29, 44),
        };
        fixture.Service.StoredAccounting.Add(existing);
        fixture.Service.SaveAccountingException = SqlExceptionFactory.Create(-2, "timeout");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenAccountingDialogAsync", existing);
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var editor = provider.FindComponent<EquipmentAccountingDialog>();
        editor.Instance.Value.IsMaterial = true;
        editor.Instance.Value.DateFrom = DateTime.Today;
        editor.Instance.Value.Notes = "unknown draft";
        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await pending;

        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));
        Assert.Contains("unknown draft", GetPrivate(page.Instance, "_restoredEditorState")!.ToString());
        await InvokeHandler(page, "RefreshAsync");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayDraftStore.discard");

        var reopenedAdded = TrackNextDialog(context);
        var reopenedPending = InvokeHandler(page, "OpenAccountingDialogAsync", existing);
        var reopenedReference = await reopenedAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reopened = provider.FindComponent<EquipmentAccountingDialog>();
        Assert.Equal("unknown draft", reopened.Instance.Value.Notes);
        Assert.Equal(existing.ChangedAt, reopened.Instance.Value.ChangedAt);
        Assert.True(PrivateBool(page.Instance, "_draftOperationUnresolved"));

        await provider.InvokeAsync(() => reopenedReference.Close(DialogResult.Cancel()));
        await reopenedPending;
    }

    /// <summary>После commit ошибка refresh закрывает редактор как записанный; повтор выполняет только SELECT.</summary>
    [Fact]
    public async Task SaveAccountingRefreshFailure_DoesNotRepeatWrite()
    {
        var fixture = CreateFixture();
        var existing = new EquipmentAccounting
        {
            Id = 41, EquipmentId = 7, OwnerId = 1, Notes = "old",
            ChangedAt = new DateTime(2026, 11, 22, 18, 29, 44),
        };
        fixture.Service.StoredAccounting.Add(existing);
        fixture.Service.AccountingRefreshException = SqlExceptionFactory.Create(53, "read after commit");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenAccountingDialogAsync", existing);
        await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var editor = provider.FindComponent<EquipmentAccountingDialog>();
        editor.Instance.Value.IsMaterial = true;
        editor.Instance.Value.DateFrom = DateTime.Today;
        editor.Instance.Value.Notes = "committed draft";

        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fixture.Service.SaveAccountingCalls);
        Assert.Equal(1, fixture.Service.AccountingRefreshCalls);
        Assert.Equal("committed draft", fixture.Service.StoredAccounting[0].Notes);
        Assert.Contains("Сохранение бухгалтерского учёта: запись подтверждена, но экран не обновлён", page.Markup);

        await InvokeHandler(page, "RefreshAccountingAsync");

        Assert.Equal(1, fixture.Service.SaveAccountingCalls);
        Assert.Equal(2, fixture.Service.AccountingRefreshCalls);
    }

    /// <summary>Обновление бухучёта сохраняет изменённые владельца, арендатора и документы основной формы.</summary>
    [Fact]
    public async Task RefreshAccounting_PreservesModifiedMainFields()
    {
        var fixture = CreateFixture();
        fixture.Service.StoredAccounting.Add(new EquipmentAccounting
        {
            EquipmentId = 7, OwnerId = 20, TenantId = 30, ReceiptDocumentId = 40, WriteOffDocumentId = 50,
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).TenantId = 999;
        CurrentModel(page.Instance).DocumentReceiptOuId = 888;

        await InvokeHandler(page, "RefreshAccountingAsync");

        Assert.Equal(20, CurrentModel(page.Instance).OwnerId);
        Assert.Equal(999, CurrentModel(page.Instance).TenantId);
        Assert.Equal(888, CurrentModel(page.Instance).DocumentReceiptOuId);
        Assert.Equal(50, CurrentModel(page.Instance).DocumentWriteOffId);
        Assert.True(IsModified(page.Instance));
    }

    /// <summary>Дочернее сохранение без основного черновика обновляет snapshot и не оставляет форму dirty.</summary>
    [Fact]
    public async Task RefreshAccountingWithoutDraft_UpdatesOriginalSnapshot()
    {
        var fixture = CreateFixture();
        var changedAt = new DateTime(2026, 11, 22, 18, 47, 6);
        fixture.Service.StoredAccounting.Add(new EquipmentAccounting
        {
            EquipmentId = 7, OwnerId = 20, TenantId = 30, ReceiptDocumentId = 40, WriteOffDocumentId = 50,
            ChangedAt = changedAt,
        });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "RefreshAccountingAsync");

        Assert.Equal(20, CurrentModel(page.Instance).OwnerId);
        Assert.Equal(30, CurrentModel(page.Instance).TenantId);
        Assert.Equal(40, CurrentModel(page.Instance).DocumentReceiptOuId);
        Assert.Equal(50, CurrentModel(page.Instance).DocumentWriteOffId);
        Assert.False(IsModified(page.Instance));
        Assert.Equal(changedAt.AddMinutes(180),
            ((Dictionary<DateTime, DateTime>)GetPrivate(page.Instance, "_localDates")!)[changedAt]);
    }

    /// <summary>Восстановление того же типа после Escape не перечитывает и не очищает телефонные правки.</summary>
    [Fact]
    public async Task RestoringSameType_PreservesEditedPhoneList()
    {
        var fixture = CreateFixture(phones: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var phones = (List<EquipmentPhoneLink>)GetPrivate(page.Instance, "_phones")!;
        phones.Add(new EquipmentPhoneLink { PhoneId = 11, EquipmentId = 7, Notes = "edited" });
        var reads = fixture.Service.GetPhonesCalls;
        var selectedType = GetPrivate(page.Instance, "_selectedType");

        await InvokeHandler(page, "OnTypeChanged", selectedType);

        Assert.Single(phones);
        Assert.Equal("edited", phones[0].Notes);
        Assert.Equal(reads, fixture.Service.GetPhonesCalls);
    }

    /// <summary>Просмотр модели открывает отдельную вкладку и не закрывает форму оборудования.</summary>
    [Fact]
    public async Task ModelView_OpensNewTabWithoutClosingEquipmentForm()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "OpenModelViewAsync", new EquipmentModel { Id = 945 });

        var open = Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "clayOpen");
        Assert.Contains("id=945", open.Arguments[0]?.ToString());
        Assert.Equal("_blank", open.Arguments[1]);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
        Assert.Equal("CLAY_TEST_7", FindSn(page).GetAttribute("value"));
    }

    /// <summary>Редактирование истории расположений и ответственных доступно только у последней строки.</summary>
    [Fact]
    public async Task HistoricalPeriods_AllowEditingOnlyLastRow()
    {
        var fixture = CreateFixture();
        for (var day = 1; day <= 3; day++)
        {
            fixture.Service.StoredLocations.Add(new EquipmentLocationPeriod
            {
                EquipmentId = 7, LocationId = 55, FromUtc = new DateTime(2026, 11, day)
            });
            fixture.Service.StoredResponsibles.Add(new EquipmentResponsiblePeriod
            {
                EquipmentId = 7, EmployeeId = 4, FromUtc = new DateTime(2026, 11, day),
                ToUtc = new DateTime(2026, 11, day + 1)
            });
        }
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.Equal(3, page.FindAll("#equipment-locations tbody tr").Count);
        Assert.Equal(3, page.FindAll("#equipment-responsibles tbody tr").Count);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#equipment-locations tbody button")));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#equipment-responsibles tbody button")));
    }

    /// <summary>Учётные записи отображаются только для MAC-типа и роли 102.</summary>
    [Fact]
    public async Task AccountsVisibility_RequiresMacTypeAndDutyRole()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        Assert.NotNull(page.Find(".equipment-accounts-toggle"));
        Assert.Empty(page.FindAll("#equipment-accounts"));
        page.Find(".equipment-accounts-toggle").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#equipment-accounts")));
        Assert.Empty(page.FindAll(".equipment-accounts-toggle"));

        fixture.Service.Roles.Remove(102);
        await InvokeHandler(page, "LoadCurrentAsync", 7);
        Assert.Empty(page.FindAll("#equipment-accounts"));
    }

    /// <summary>Команда рабочей сети находится в меню «Операции» и требует роль 103.</summary>
    [Fact]
    public async Task NetworkCommand_RequiresComputerTypeAndSupportRole()
    {
        var fixture = CreateFixture(computer: true);
        fixture.Service.Roles.Add(103);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        Assert.Contains("Операции", page.Markup);
        Assert.Contains("Операции", page.FindAll(".clay-form-toolbar-button")[0].TextContent);
        Assert.DoesNotContain("Подключить сеть", page.Markup);

        fixture.Service.Roles.Remove(103);
        await InvokeHandler(page, "LoadCurrentAsync", 7);
        Assert.DoesNotContain("Операции", page.Markup);
    }

    /// <summary>Добавление UPS находится в меню «Операции» и зависит от роли 43 по владельцу.</summary>
    [Fact]
    public async Task UpsCommand_RequiresInventoryRoleForOwner()
    {
        var fixture = CreateFixture(phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        Assert.Contains("Операции", page.Markup);
        Assert.DoesNotContain("Подключить UPS:", page.Markup);

        fixture.Service.RoleForPersonResult = false;
        await InvokeHandler(page, "LoadCurrentAsync", 7);
        Assert.DoesNotContain("Подключить UPS:", page.Markup);
        Assert.False(PrivateBool(page.Instance, "_canManageUps"));
        Assert.Contains("Операции", page.Markup);
        Assert.Empty(page.FindAll("#equipment-ups"));
    }

    /// <summary>Бухгалтерская секция доступна для чтения при отсутствии права изменения.</summary>
    [Fact]
    public async Task AccountingWithoutWriteAccess_RemainsVisible()
    {
        var fixture = CreateFixture();
        fixture.Service.AccountingAvailable = false;
        fixture.Service.StoredAccounting.Add(new EquipmentAccounting { EquipmentId = 7, OwnerName = "Read only owner" });
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        Assert.NotNull(page.Find("#equipment-accounting"));
        page.WaitForAssertion(() => Assert.Contains("Read only owner", page.Markup));
        Assert.False(PrivateBool(page.Instance, "_canUseAccounting"));
    }

    /// <summary>Право лицензии пересчитывается при смене владельца.</summary>
    [Fact]
    public async Task OwnerChange_RecalculatesLicenseAccess()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_selectedType", new EquipmentType { Id = 200, HasSoftwareCharacteristics = 1 });

        await InvokeHandler(page, "OnOwnerChangedAsync", 2);
        Assert.True(PrivateBool(page.Instance, "_canEditLicense"));
        fixture.Service.RoleForPersonResult = false;
        await InvokeHandler(page, "OnOwnerChangedAsync", 3);
        Assert.False(PrivateBool(page.Instance, "_canEditLicense"));
    }

    /// <summary>Кнопка конфигурации следует роли 102 независимо от роли редактирования 43.</summary>
    [Fact]
    public async Task PhoneConfigAccess_UsesRole102InsteadOfEditRole()
    {
        var fixture = CreateFixture(phoneConfig: true);
        fixture.Service.Roles.Clear();
        fixture.Service.Roles.Add(102);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var button = page.FindAll("button").Single(item => item.TextContent.Trim() == "Конфигурация");
        Assert.False(button.HasAttribute("disabled"));

        fixture.Service.Roles.Clear();
        fixture.Service.Roles.Add(43);
        await InvokeHandler(page, "LoadCurrentAsync", 7);
        button = page.FindAll("button").Single(item => item.TextContent.Trim() == "Конфигурация");
        Assert.True(button.HasAttribute("disabled"));
    }

    /// <summary>Явная настройка сохранённого IP-телефона не выполняет SQL UPDATE.</summary>
    [Fact]
    public async Task ConfigureSavedPhone_UsesAtsCommandWithoutUpdate()
    {
        var fixture = CreateFixture(phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await InvokeHandler(page, "ConfigurePhoneEquipmentAsync");

        Assert.Equal(1, fixture.Service.SyncCalls);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Contains("Настройка оборудования выполнена", page.Markup);
    }

    /// <summary>Dirty-ввод не теряется и не сохраняется скрыто ради настройки телефона.</summary>
    [Fact]
    public async Task ConfigureDirtyPhone_KeepsInputAndSkipsCommand()
    {
        var fixture = CreateFixture(phoneConfig: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_PHONE_DRAFT");

        await InvokeHandler(page, "ConfigurePhoneEquipmentAsync");

        Assert.Equal(0, fixture.Service.SyncCalls);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Equal("CLAY_TEST_PHONE_DRAFT", FindSn(page).GetAttribute("value"));
        Assert.Contains("Сохраните оборудование перед настройкой", page.Markup);
    }

    /// <summary>Global102 позволяет показать и скрыть пароль без роли редактирования43.</summary>
    [Fact]
    public async Task AccountPassword_ShowHideDoesNotRequireEditRole()
    {
        var fixture = CreateFixture(network: true);
        fixture.Service.Roles.Clear();
        fixture.Service.Roles.Add(102);
        var account = new EquipmentAccount { EquipmentId = 7, Login = "reader", Notes = "note" };
        fixture.Service.StoredAccounts.Add(account);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.Find(".equipment-accounts-toggle").Click();

        await InvokeHandler(page, "ToggleAccountPasswordAsync", account);

        page.WaitForAssertion(() => Assert.Contains("secret", page.Markup));
        Assert.Equal(1, fixture.Service.AccountPasswordCalls);
        Assert.False(PrivateBool(page.Instance, "_canEdit"));

        await InvokeHandler(page, "ToggleAccountPasswordAsync", account);

        Assert.DoesNotContain("secret", page.Markup);
        Assert.Equal(1, fixture.Service.AccountPasswordCalls);
    }

    /// <summary>Компьютерная секция сохраняет отдельные модули и даты стандартных конфигураций.</summary>
    [Fact]
    public async Task ComputerGrid_BuildsDynamicModuleAndConfigurationColumns()
    {
        var fixture = CreateFixture(computer: true);
        fixture.Service.StoredComputers.Add(new()
        {
            Date = new DateTime(2026, 1, 1),
            RamMb = 3072,
            RamComponents = [1024, 2048],
            HdGb = 1000,
            HdComponents = [1000],
            SsdGb = 500,
            SsdComponents = [250, 250],
            Configuration1Id = 1,
            Configuration1Name = "Офисная",
            Configuration1Date = new DateTime(2026, 1, 2),
        });
        await using var context = fixture.Context;

        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.Contains("2048", page.Find("#equipment-computer tbody tr").TextContent));
        var grid = page.Find("#equipment-computer");
        var headers = grid.QuerySelectorAll("thead th").Select(column => column.TextContent.Trim())
            .Where(title => title.Length > 0).ToArray();

        Assert.Equal(new[]
        {
            "С", "RAM", "RAM1", "RAM2", "HD", "HD1", "SSD", "SSD1", "SSD2",
            "Стандартная конфигурация 1", "Лицензия", "Примечания", "Изменил", "Изменено",
        }, headers);
        var row = grid.QuerySelector("tbody tr")!.TextContent;
        Assert.Contains("2048", row);
        Assert.Contains("Офисная [02.01.26]", row);
        Assert.DoesNotContain("clay-grid-search", page.Markup);
        Assert.DoesNotContain("clay-grid-paginator", page.Markup);
    }

    /// <summary>Телефонная секция выводит ссылки, условное подключение, просрочку и предупреждение отеля.</summary>
    [Fact]
    public async Task PhoneGrid_RestoresLegacyPresentation()
    {
        var fixture = CreateFixture(phones: true);
        fixture.Service.StoredPhones.AddRange(
        [
            new()
            {
                PhoneId = 11,
                Description = "101",
                IsDefault = true,
                ConnectionEquipmentId = 55,
                ReturnDate = DateTime.UtcNow.Date.AddDays(-1),
                ChangedByName = "Тестов А.",
                ChangedAt = new DateTime(2026, 1, 1, 12, 0, 0),
            },
            new() { PhoneId = 12, Description = "102" },
        ]);
        fixture.Service.HotelPhoneCount = 2;
        await using var context = fixture.Context;

        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var phoneLink = page.Find("a[title^='Изменил:']");
        Assert.Equal("http://localhost/Telefon/Tel?id=11", phoneLink.GetAttribute("href"));
        Assert.Contains("01.01.2026 15:00:00", phoneLink.GetAttribute("title"));
        Assert.Equal("http://localhost/Equipment/EquipmentForm?id=55", page.Find("a[title^='Оборудование подключения']").GetAttribute("href"));
        Assert.Contains("(основной на аппарате)", page.Markup);
        Assert.Contains("clay-grid-hide-header", page.Find("#equipment-phones").ClassName);
        var rows = page.FindAll("#equipment-phones tbody tr");
        Assert.Equal(string.Empty, rows[1].QuerySelectorAll("td")[2].TextContent.Trim());
        Assert.Single(page.FindAll("tr.equipment-overdue"));
        Assert.Contains("может быть только один телефонный номер", page.Markup);
    }

    /// <summary>ПО загружается по компьютерному признаку и ссылается на карточку ПО, а не установки.</summary>
    [Fact]
    public async Task SoftwareGrid_UsesComputerCharacteristicAndSoftwareId()
    {
        var fixture = CreateFixture(computer: true);
        fixture.Service.StoredSoftware.Add(new()
        {
            Id = 1,
            SoftwareId = 66,
            SoftwareName = "Office",
            Installed = "Office 2026",
            InstalledAt = new DateTime(2026, 1, 1),
        });
        await using var context = fixture.Context;

        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        var link = page.Find("a[title='Открыть форму редактирования информации о ПО']");
        Assert.Equal("http://localhost/Equipment/EquipmentForm?id=66", link.GetAttribute("href"));
        Assert.Equal("Office 2026", link.TextContent);
    }

    /// <summary>Ошибка любого запроса UPS не открывает пустой диалог и сохраняет черновик формы.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UpsLoadSqlFailure_KeepsDraftAndDoesNotOpenDialog(int failedRequest)
    {
        var fixture = CreateFixture();
        fixture.Service.UpsReadException = SqlExceptionFactory.Create(53, "connection error");
        fixture.Service.UpsReadFailureAt = failedRequest;
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);
        FindSn(page).Input("CLAY_TEST_UPS_DRAFT");

        await InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 10, ServerEquipmentId = 7 });

        page.WaitForAssertion(() => Assert.Contains("Не удалось загрузить подключение UPS", page.Markup));
        Assert.Empty(provider.FindComponents<EquipmentUpsDialog>());
        Assert.Equal("CLAY_TEST_UPS_DRAFT", FindSn(page).GetAttribute("value"));
        Assert.Null(GetPrivate(page.Instance, "_pendingPhoneSync"));
    }

    /// <summary>После восстановления соединения повторное открытие UPS создаёт диалог с новым снимком.</summary>
    [Fact]
    public async Task UpsLoadRetryAfterRecovery_OpensDialog()
    {
        var fixture = CreateFixture();
        fixture.Service.UpsReadException = SqlExceptionFactory.Create(53, "connection error");
        fixture.Service.UpsReadFailureAt = 1;
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);
        await InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 10, ServerEquipmentId = 7 });
        Assert.Empty(provider.FindComponents<EquipmentUpsDialog>());

        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 10, ServerEquipmentId = 7 });
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(provider.FindComponents<EquipmentUpsDialog>());

        await page.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await pending;
    }

    /// <summary>Поздний ответ загрузки UPS прежнего ID не открывает диалог для новой записи.</summary>
    [Fact]
    public async Task LateUpsLoadAfterIdChange_DoesNotOpenDialog()
    {
        var fixture = CreateFixture();
        fixture.Service.DeferUpsServers = true;
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);

        var pending = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 10, ServerEquipmentId = 7 });
        await fixture.Service.UpsServersRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        fixture.Service.UpsServersCompletion.SetResult();
        await pending;

        Assert.Empty(provider.FindComponents<EquipmentUpsDialog>());
        Assert.Equal(8, CurrentModel(page.Instance).Id);
    }

    /// <summary>Ошибка соединения при сохранении UPS показывает неизвестный результат и не обновляет родительский список.</summary>
    [Fact]
    public async Task SaveUpsConnectivity_DoesNotRunSuccessRefresh()
    {
        var fixture = CreateFixture();
        fixture.Service.UpsSaveException = SqlExceptionFactory.Create(10054, "connection lost");
        await using var context = fixture.Context;
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canManageUps", true);
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenUpsDialogAsync", new UpsServerLink { UpsEquipmentId = 10, ServerEquipmentId = 7 });
        await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var editor = provider.FindComponent<EquipmentUpsDialog>();

        await editor.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        editor.WaitForAssertion(() => Assert.Contains("Результат команды «Сохранение подключения UPS» неизвестен", editor.Markup));
        Assert.Equal(0, fixture.Service.UpsRefreshCalls);
    }

    /// <summary>Детализация доступна для Билайн и открывается через clayOpen без навигации родителя.</summary>
    [Fact]
    public async Task SimDetail_Beeline_OpensExternalFormWithoutNavigation()
    {
        var fixture = CreateFixture();
        fixture.Service.SimDetailUrl = "https://billing.test/phone?Phone=79000000001";
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_selectedType", new EquipmentType { Id = 27, HasSimCharacteristics = 1 });
        SetPrivate(page.Instance, "_simCards", new List<EquipmentSimCard>
        {
            new() { EquipmentId = 7, ExecutorPersonId = 908 },
        });
        ((ConfigurationManager)context.Services.GetRequiredService<IConfiguration>())["URI_accounting_phone_form"] =
            "https://billing.test/phone";
        page.Render();

        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Детализация").ClickAsync();

        var invocation = context.JSInterop.Invocations.Single(call => call.Identifier == "clayOpen");
        Assert.Equal(fixture.Service.SimDetailUrl, invocation.Arguments[0]);
        Assert.Equal("equipment-sim-detail", invocation.Arguments[1]);
        Assert.Equal("http://localhost/Equipment/EquipmentForm?id=7", fixture.Navigation.Uri);
    }

    /// <summary>Блокировка, предупреждение и суммы услуг SIM доступны в строках грида.</summary>
    [Fact]
    public async Task SimServices_RenderWarningsPlannedDatesAndTotals()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_selectedType", new EquipmentType { Id = 27, HasSimCharacteristics = 1 });
        SetPrivate(page.Instance, "_simServices", new List<EquipmentSimService>
        {
            new()
            {
                ServiceTypeId = "_BLOCK", ServiceName = "Блокировка", DateFrom = DateTime.Today.AddDays(-2),
                PlannedDateFrom = DateTime.Today.AddDays(-1), DailyCost = 2.5m, MonthlyCost = 10m,
            },
            new()
            {
                ServiceTypeId = "_TEST", ServiceName = "Услуга", DateFrom = DateTime.Today.AddDays(-2),
                MustBeDisabled = 1, RequiredServiceDescription = "Требуется отключение", DailyCost = 1.5m, MonthlyCost = 5m,
            },
        });
        page.Render();

        Assert.Contains("заблокировано", page.Markup);
        Assert.Contains("План подкл.", page.Markup);
        var totals = page.Find("[data-testid='sim-service-totals']").TextContent;
        Assert.Contains("4,00", totals);
        Assert.Contains("15,00", totals);
        Assert.NotEmpty(page.FindAll("tr.mud-theme-error"));
    }

    /// <summary>DisplayNet скрывает звёздные имена, выделяет warning и применяет исключение switch.</summary>
    [Fact]
    public async Task NetworkDiagnostics_RenderWarningStarFilterAndSwitchHide()
    {
        var fixture = CreateFixture(network: true);
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        CurrentModel(page.Instance).MacAddress2 = null;
        SetPrivate(page.Instance, "_networkPorts", new List<EquipmentNetworkPortLink>
        {
            new() { Id = 11, NetworkName = "server-a", Port = 1 },
            new() { Id = 12, NetworkName = "*hidden", Port = 2 },
        });
        page.Render();

        var link = Assert.Single(page.FindAll("[data-testid='equipment-network-ports'] a"));
        Assert.Equal("server-a_1", link.TextContent.Trim());
        Assert.Contains("mud-error-text", link.ClassList);

        CurrentModel(page.Instance).EquipmentTypeId = 33;
        SetPrivate(page.Instance, "_networkPorts", new List<EquipmentNetworkPortLink>
        {
            new() { Id = 11, NetworkName = "server-a", Port = 1 },
            new() { Id = 12, NetworkName = "server-b", Port = 2 },
            new() { Id = 13, NetworkName = "server-c", Port = 3 },
        });
        page.Render();
        Assert.Empty(page.FindAll("[data-testid='equipment-network-ports']"));
    }

    /// <summary>Успешный UPDATE с последующим not-found при reload не запускает attach и не даёт NullReference.</summary>
    [Fact]
    public async Task NetworkAttach_AfterReloadReturnsNull_DoesNotRunAttach()
    {
        var fixture = CreateFixture(network: true);
        fixture.Service.ReturnNullAfterUpdate = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canChangeNetwork", true);
        CurrentModel(page.Instance).Description = "draft";

        await InvokeHandler(page, "SetNetworkAttachmentAsync", true);

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.Equal(0, fixture.Service.AttachNetworkCalls);
        Assert.True(PrivateBool(page.Instance, "_notFound"));
    }

    private static Fixture CreateFixture(
        bool phones = false,
        bool network = false,
        bool phoneConfig = false,
        bool computer = false,
        IReadOnlyList<DocumentLookup>? writeOffActs = null,
        int? writeOffDocumentId = null)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        var js = new DeferredJSRuntime(context.JSInterop.JSRuntime);
        context.Services.AddSingleton<IJSRuntime>(js);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<int>("clayGridTimeZone.getOffsetMinutes", _ => true).SetResult(180);
        context.JSInterop.Setup<long>("clayGridTimeZone.localDateTimeToUtcMilliseconds", _ => true).SetResult(0L);
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("test-registration", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"test-attempt","kind":"test","status":"running","revision":1,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Services.AddScoped<DbManager>(_ => CreateDb());
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton(new ClayFormWindowService(js));
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=7");
        var configuration = new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost/user",
            ["URI_model_form"] = "http://localhost/Equipment/ModelEquipmentForm",
            ["URI_equipment_form"] = "http://localhost/Equipment/EquipmentForm",
            ["URI_net_form"] = "http://localhost/Net/Net",
            ["URI_tel_form"] = "http://localhost/Telefon/Tel",
        };
        var service = new FakeEquipmentFormService(CreateDb())
        {
            PhoneConfig = phoneConfig,
            WriteOffDocumentId = writeOffDocumentId
        };
        var actRows = (writeOffActs ?? []).Select(item => Script.Row(
            ("КодДокумента", item.Id), ("НазваниеДокумента", item.Name),
            ("НомерДокумента", item.Number), ("ДатаДокумента", item.Date))).ToArray();
        var writeOffEntries = Enumerable.Range(0, 8).Select(_ => Script.Rows(actRows)).ToArray();
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton<EquipmentFormService>(service);
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(CreateDb(
            Script.Rows(TypeRow(phones, network, phoneConfig, computer)), Script.Rows(TypeRow(phones, network, phoneConfig, computer)),
            Script.Rows(TypeRow(phones, network, phoneConfig, computer)), Script.Rows(TypeRow(phones, network, phoneConfig, computer)))));
        context.Services.AddSingleton(new EquipmentModelSelectDataSource(CreateDb(
            Script.Rows(ModelRow(network, phoneConfig)),
            Script.Rows(ModelRow(network, phoneConfig)),
            Script.Rows(ModelRow(network, phoneConfig)),
            Script.Rows(ModelRow(network, phoneConfig)))));
        context.Services.AddSingleton(new PhonePoolSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new PhoneNumberSelectDataSource(CreateDb(
            Script.Rows(PhoneNumberRow()), Script.Rows(PhoneNumberRow()), Script.Rows(PhoneNumberRow()))));
        context.Services.AddSingleton(new PersonSelectDataSource(CreateDb(
            Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()), Script.Rows(PersonRow()))));
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb(
            Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов Администратор"))))));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb(
            Script.Rows(Script.Row(("КодРасположения", 55), ("РасположениеPath1", "На руках"))))));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("receipt", new DocumentSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("writeoff", new DocumentSelectDataSource(CreateDb(writeOffEntries)));
        context.Services.AddSingleton(new AccountingPersonSelectDataSource(CreateDb(Script.Rows(PersonRow()))));
        context.Services.AddKeyedSingleton<AccountingDocumentSelectDataSource>("accountingReceipt",
            new AccountingDocumentSelectDataSource(CreateDb(), false));
        context.Services.AddKeyedSingleton<AccountingDocumentSelectDataSource>("accountingWriteOff",
            new AccountingDocumentSelectDataSource(CreateDb(), true));
        context.Services.AddKeyedSingleton<UpsEquipmentSelectDataSource>("ups", new UpsEquipmentSelectDataSource(CreateDb(), true));
        context.Services.AddKeyedSingleton<UpsEquipmentSelectDataSource>("server", new UpsEquipmentSelectDataSource(CreateDb(), false));
        return new Fixture(context, service, navigation, js);
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

    private static async Task CreatePendingSync(
        Fixture fixture,
        IRenderedComponent<EquipmentForm> page)
    {
        fixture.Service.SyncException = new InvalidOperationException("АТС недоступна");
        WaitForSn(page, "CLAY_TEST_7");
        await InvokeHandler(page, "OnPhoneToAddChangedAsync", 11);
        await InvokeSave(page, false);
        Assert.NotNull(GetPrivate(page.Instance, "_pendingPhoneSync"));
    }

    private static TaskCompletionSource<IDialogReference> TrackNextDialog(BunitContext context)
    {
        var result = new TaskCompletionSource<IDialogReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Services.GetRequiredService<IDialogService>().DialogInstanceAddedAsync += reference =>
        {
            result.TrySetResult(reference);
            return Task.CompletedTask;
        };
        return result;
    }

    private static async Task InvokeHandler(
        IRenderedComponent<EquipmentForm> page,
        string name,
        params object?[] args) =>
        await page.InvokeAsync(async () =>
        {
            var method = typeof(EquipmentForm).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (method.Invoke(page.Instance, args) is Task task)
                await task;
        });

    private static EquipmentEntity CurrentModel(EquipmentForm page) =>
        (EquipmentEntity)GetPrivate(page, "_model")!;

    private static EquipmentEntity OriginalEquipment(IRenderedComponent<EquipmentForm> page)
    {
        var snapshot = (string)GetPrivate(page.Instance, "_originalSnapshot")!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!["Equipment"]!;
        return System.Text.Json.JsonSerializer.Deserialize<EquipmentEntity>(node.ToJsonString())!;
    }

    private static string DraftJson(EquipmentEntity model, string originalSnapshot) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Model = model,
            WriteOffDate = (DateTime?)null,
            WriteOffTime = (TimeSpan?)null,
            Phones = Array.Empty<EquipmentPhoneLink>(),
            OriginalPhones = Array.Empty<EquipmentPhoneLink>(),
            OriginalSnapshot = originalSnapshot,
            PendingPhoneSync = (object?)null,
            ActiveEditor = (object?)null,
            ActiveEditorState = (string?)null,
            ActiveUpsServers = (object?)null,
            ActiveUpsTests = (object?)null,
        });

    private static void SetDraftMainOperation(IRenderedComponent<EquipmentForm> page, string snapshot)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(new
        {
            Kind = "equipment-save",
            EquipmentId = 7,
            Editor = (string?)null,
            Origin = "main",
            Intent = new { ExpectedSnapshot = snapshot, AfterSnapshot = snapshot },
        });
        var operation = new ClayDraftOperation(1, "test-attempt", "equipment-save",
            ClayDraftOperationStatus.Unknown, 1, data,
            [new ClayDraftOperationScope("main", 0, new Dictionary<string, long>())], true, 0);
        SetPrivate(page.Instance, "_draftOperation", operation);
        SetPrivate(page.Instance, "_draftOperationUnresolved", true);
        SetPrivate(page.Instance, "_draftOperationScope", "main");
    }

    private static UpsTest CloneUpsTestForTest(UpsTest source, DateTime testedAt) => new()
    {
        Id = source.Id,
        UpsEquipmentId = source.UpsEquipmentId,
        TestedAt = testedAt,
        BatteryTime = source.BatteryTime,
        Servers = source.Servers,
        Issues = source.Issues,
        ChangedAt = source.ChangedAt,
        ChangedByName = source.ChangedByName,
    };

    private static object? GetPrivate(EquipmentForm page, string name) => typeof(EquipmentForm)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page);

    private static void SetPrivate(EquipmentForm page, string name, object? value) => typeof(EquipmentForm)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(page, value);

    private static bool PrivateBool(EquipmentForm page, string name) => (bool)GetPrivate(page, name)!;

    private static bool BoolProperty(EquipmentForm page, string name) => (bool)typeof(EquipmentForm)
        .GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page)!;

    private static int IntProperty(EquipmentForm page, string name) => (int)typeof(EquipmentForm)
        .GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page)!;

    private static bool IsModified(EquipmentForm page) => (bool)typeof(EquipmentForm)
        .GetProperty("IsModified", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(page)!;

    private static bool ValidateBusinessRules(EquipmentForm page) => (bool)typeof(EquipmentForm)
        .GetMethod("ValidateBusinessRules", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(page, null)!;

    private static Dictionary<string, object?> TypeRow(
        bool phones = false, bool network = false, bool phoneConfig = false, bool computer = false) => Script.Row(
        ("КодТипаОборудования", phoneConfig ? 30 : 103), ("ТипОборудования", phoneConfig ? "IP-телефон" : "Шторы"),
        ("SNобязателен", (byte)0), ("ЕстьХарактеристикиМонитора", (byte)0),
        ("ЕстьТелефонныйНомер", phones ? (byte)1 : (byte)0), ("ЕстьMACадрес", network ? (byte)1 : (byte)0),
        ("ЕстьХарактеристикиКомпьютера", computer ? (byte)1 : (byte)0),
        ("ЕстьСетевоеИмя", network ? (byte)1 : (byte)0));

    private static Dictionary<string, object?> ModelRow(bool network = false, bool phoneConfig = false) => Script.Row(
        ("КодМоделиОборудования", 945), ("МодельОборудования", phoneConfig ? "Phone" : "Шторы"),
        ("КодТипаОборудования", phoneConfig ? 30 : 103), ("Net", network ? 1 : 0),
        ("ШаблонТелефона", phoneConfig ? "template" : ""), ("PN", "PN-945"),
        ("Мультимедиа", true), ("РазмерМонитора", 24.0), ("CPU", "CPU-X"),
        ("НазваниеNet", "Ethernet"), ("КодТипаПамяти", 1), ("БанковПамяти", 2),
        ("ТипПамяти", "DDR4"), ("Драйвер", "Driver-X"), ("Ресурс", "100000 часов"),
        ("Примечания", "Model notes"));

    private static Dictionary<string, object?> PhoneNumberRow() => Script.Row(
        ("КодТелефонногоНомера", 11), ("Телефон", "101"),
        ("КодТелефоннойСтанции", 2), ("XorcomName", ""));

    private static Dictionary<string, object?> PersonRow() => Script.Row(
        ("КодЛица", 1), ("Кличка", "Тестовая организация"));

    private sealed record Fixture(
        BunitContext Context,
        FakeEquipmentFormService Service,
        FakeNavigationManager Navigation,
        DeferredJSRuntime Js);

    private sealed class FakeEquipmentFormService : EquipmentFormService
    {
        private readonly TaskCompletionSource _getRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _updateRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeEquipmentFormService(DbManager db) : base(db) { }
        public EquipmentEntity? Updated { get; private set; }
        public Exception? SaveException { get; set; }
        public Exception? SyncException { get; set; }
        public Exception? ResponsibleException { get; set; }
        public EquipmentResponsiblePeriod? ResponsibleUpdated { get; private set; }
        public bool DeferredUpdate { get; set; }
        public bool DeferredInitialLoad { get; set; }
        public Exception? LoadException { get; set; }
        public Exception? DetailsException { get; set; }
        public bool PhoneConfig { get; set; }
        public int? WriteOffDocumentId { get; set; }
        public bool RoleForPersonResult { get; set; } = true;
        public bool AccountingAvailable { get; set; } = true;
        public int UpdateCalls { get; private set; }
        public int AttachNetworkCalls { get; private set; }
        public bool ReturnNullAfterUpdate { get; set; }
        public int AddAccountCalls { get; private set; }
        public int DeleteSimCalls { get; private set; }
        public int GetPhonesCalls { get; private set; }
        public List<int> Requests { get; } = [];
        public HashSet<int> Roles { get; } = [42, 43, 102];
        public Dictionary<int, DateTime?> WriteOffDates { get; } = [];
        public List<EquipmentResponsiblePeriod> StoredResponsibles { get; } = [];
        public List<EquipmentLocationPeriod> StoredLocations { get; } = [];
        public List<(EquipmentLocationPeriod Location, EquipmentResponsiblePeriod? Responsible)> LocationSaves { get; } = [];
        public List<EquipmentAccounting> StoredAccounting { get; } = [];
        public List<(EquipmentResponsiblePeriod Current, EquipmentResponsiblePeriod? Next, EquipmentLocationPeriod? Location)> WithdrawCalls { get; } = [];
        public List<EquipmentPhoneLink> StoredPhones { get; private set; } = [];
        public List<EquipmentSoftwareInstallation> StoredSoftware { get; } = [];
        public List<EquipmentComputerSnapshot> StoredComputers { get; } = [];
        public int HotelPhoneCount { get; set; }
        public List<(int[] Original, int[] Current)> PhoneSaves { get; } = [];
        public int UpdateWithPhonesCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public IReadOnlyList<PhoneSyncResult>? SyncResults { get; set; }
        public IReadOnlyList<PhoneSyncOperation> LastSyncOperations { get; private set; } = [];
        public TaskCompletionSource UpdateCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialLoadCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialLoadReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EquipmentEntity? NextModel { get; set; }
        public int? EmployeeId { get; set; }

        public override async Task<EquipmentEntity?> GetAsync(int id)
        {
            Requests.Add(id);
            if (NextModel is not null)
            {
                var next = NextModel;
                NextModel = null;
                return next;
            }
            if (ReturnNullAfterUpdate && UpdateCalls > 0)
                return null;
            if (LoadException is not null)
            {
                var exception = LoadException;
                LoadException = null;
                throw exception;
            }
            if (DeferredInitialLoad && id == 7)
            {
                _getRequested.TrySetResult();
                await InitialLoadCompletion.Task;
                InitialLoadReturned.TrySetResult();
            }
            return new EquipmentEntity
            {
                Id = id, EquipmentTypeId = PhoneConfig ? 30 : 103, ModelId = 945, OwnerId = 1, TenantId = id,
                LocationId = 55, EmployeeId = EmployeeId, SerialNumber = $"CLAY_TEST_{id}", Description = $"Copy{id}",
                MacAddress = "001122334455", WriteOffAtUtc = WriteOffDates.GetValueOrDefault(id),
                DocumentWriteOffId = WriteOffDocumentId,
                ChangedById = 4, ChangedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0), ChangedByName = "Тестов А.",
            };
        }

        public override Task<EquipmentFormReadResult> GetFormDataAsync(EquipmentFormReadRequest request) =>
            GetFormDataAsync(request, null);

        /// <summary>Собирает пакетный результат из тех же тестовых данных, не обращаясь к БД.</summary>
        public override async Task<EquipmentFormReadResult> GetFormDataAsync(
            EquipmentFormReadRequest request, DateTime? businessToday)
        {
            if (DetailsException is not null)
            {
                var exception = DetailsException;
                DetailsException = null;
                throw exception;
            }
            var phones = request.IncludePhones
                ? await GetPhonesAsync(request.EquipmentId)
                : (IReadOnlyList<EquipmentPhoneLink>)[];
            return new EquipmentFormReadResult
            {
                Phones = phones,
                HotelPhoneCount = request.IncludeHotelPhoneCount && phones.Count > 0 ? HotelPhoneCount : 0,
                Locations = StoredLocations.Where(item => item.EquipmentId == request.EquipmentId).ToList(),
                Responsibles = StoredResponsibles.ToList(),
                Accounts = request.IncludeAccounts ? StoredAccounts.ToList() : [],
                ComputerHistory = request.IncludeComputer ? StoredComputers.ToList() : [],
                SimCards = [],
                SimServices = [],
                SimServiceTypes = [],
                Licenses = [],
                LicenseTypes = [],
                SoftwareInstallations = request.IncludeComputer ? StoredSoftware.ToList() : [],
                Accounting = StoredAccounting.ToList(),
                RouterInfo = null,
                RouterInventory = [],
                UpsLinks = [],
                LastInventory = null,
                NetworkAttachmentCount = 0,
                NetworkPorts = request.IncludeNetworkName
                    ? [new EquipmentNetworkPortLink { Id = 11, NetworkName = "clay-test-net", Port = 12 }]
                    : [],
                NetworkNameHistory = request.IncludeNetworkName
                    ? await GetNetworkNameHistoryAsync(request.EquipmentId)
                    : []
            };
        }

        public override Task<IReadOnlyList<EquipmentResponsiblePeriod>> GetResponsiblesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentResponsiblePeriod>>(StoredResponsibles.ToList());

        public int ComputerRefreshCalls { get; private set; }
        public int ComputerSaveCalls { get; private set; }
        public TaskCompletionSource ComputerSaveRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ComputerSaveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task SaveComputerPhasedAsync(EquipmentComputerSnapshot snapshot, DateTime? today = null)
        {
            ComputerSaveCalls++;
            ComputerSaveRequested.TrySetResult();
            await ComputerSaveCompletion.Task;
            StoredComputers.Add(snapshot);
        }

        public override Task<IReadOnlyList<EquipmentComputerSnapshot>> GetComputerHistoryAsync(int equipmentId)
        {
            ComputerRefreshCalls++;
            return Task.FromResult<IReadOnlyList<EquipmentComputerSnapshot>>(StoredComputers.ToList());
        }

        public override Task<IReadOnlyList<StandardConfiguration>> GetStandardConfigurationsAsync() =>
            Task.FromResult<IReadOnlyList<StandardConfiguration>>([]);

        public List<EquipmentAccount> StoredAccounts { get; } = [];
        public string AccountPassword { get; set; } = "secret";
        public int AccountPasswordCalls { get; private set; }

        public override Task<IReadOnlyList<EquipmentAccount>> GetAccountsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentAccount>>(StoredAccounts.ToList());

        public override Task<string> GetAccountPasswordAsync(int equipmentId, string login)
        {
            AccountPasswordCalls++;
            return Task.FromResult(AccountPassword);
        }

        public string? SimDetailUrl { get; set; }

        public override Task<string?> GetSimDetailUrlAsync(int equipmentId, string baseUri) =>
            Task.FromResult(SimDetailUrl);

        public override Task<IReadOnlyList<EquipmentSimCard>> GetSimCardsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentSimCard>>([]);

        public override Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
            int equipmentId, bool showHistory, bool showRequired) =>
            GetSimServicesAsync(equipmentId, showHistory, showRequired, null);

        public Queue<TaskCompletionSource<IReadOnlyList<EquipmentSimService>>> SimServiceReadCompletions { get; } = new();
        public bool DeferSimServiceRead { get; set; }

        public override async Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
            int equipmentId, bool showHistory, bool showRequired, DateTime? businessToday)
        {
            if (DeferSimServiceRead)
                return await SimServiceReadCompletions.Dequeue().Task;
            return [];
        }

        public override Task<IReadOnlyList<SimServiceType>> GetSimServiceTypesAsync() =>
            Task.FromResult<IReadOnlyList<SimServiceType>>([]);

        public override Task<IReadOnlyList<EquipmentLicense>> GetLicensesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentLicense>>([]);

        public override Task<IReadOnlyList<LicenseType>> GetLicenseTypesAsync() =>
            Task.FromResult<IReadOnlyList<LicenseType>>([]);

        public override Task<IReadOnlyList<EquipmentSoftwareInstallation>> GetSoftwareInstallationsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentSoftwareInstallation>>(StoredSoftware.ToList());

        public override Task<IReadOnlyList<EquipmentAccounting>> GetAccountingAsync(int equipmentId)
        {
            AccountingRefreshCalls++;
            if (AccountingRefreshException is not null)
            {
                var exception = AccountingRefreshException;
                AccountingRefreshException = null;
                return Task.FromException<IReadOnlyList<EquipmentAccounting>>(exception);
            }
            return Task.FromResult<IReadOnlyList<EquipmentAccounting>>(StoredAccounting.ToList());
        }

        public override Task<EquipmentRouterInfo?> GetRouterInfoAsync(int equipmentId) =>
            Task.FromResult<EquipmentRouterInfo?>(null);

        public override Task<IReadOnlyList<EquipmentRouterInventoryItem>> GetRouterInventoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentRouterInventoryItem>>([]);

        public Exception? UpsReadException { get; set; }
        public Exception? UpsSaveException { get; set; }
        public Exception? DeleteAccountingException { get; set; }
        public int UpsReadFailureAt { get; set; }
        public int UpsRefreshCalls { get; private set; }
        public int AccountingRefreshCalls { get; private set; }
        public bool DeferUpsServers { get; set; }
        public TaskCompletionSource UpsServersRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpsServersCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IReadOnlyList<UpsServerLink>> GetUpsServersAsync(int upsEquipmentId)
        {
            if (UpsReadFailureAt == 1 && UpsReadException is not null)
            {
                var exception = UpsReadException;
                UpsReadException = null;
                throw exception;
            }
            if (DeferUpsServers)
            {
                UpsServersRequested.TrySetResult();
                await UpsServersCompletion.Task;
            }
            return [];
        }

        public override Task<IReadOnlyList<UpsTest>> GetUpsTestsAsync(int upsEquipmentId)
        {
            if (UpsReadFailureAt == 2 && UpsReadException is not null)
                throw UpsReadException;
            return Task.FromResult<IReadOnlyList<UpsTest>>([]);
        }

        public IReadOnlyCollection<UpsTest>? LastUpsCurrentTests { get; private set; }

        public override Task SaveUpsDetailsAsync(
            int upsEquipmentId,
            int sourceServerEquipmentId,
            IReadOnlyCollection<UpsServerLink> originalServers,
            IReadOnlyCollection<UpsServerLink> currentServers,
            IReadOnlyCollection<UpsTest> originalTests,
            IReadOnlyCollection<UpsTest> currentTests)
        {
            LastUpsCurrentTests = currentTests.ToList();
            return UpsSaveException is not null ? Task.FromException(UpsSaveException) : Task.CompletedTask;
        }

        public Exception? SaveAccountingException { get; set; }
        public Exception? AccountingRefreshException { get; set; }
        public EquipmentAccounting? LastAccountingDraft { get; private set; }
        public int SaveAccountingCalls { get; private set; }

        public override Task SaveAccountingAsync(EquipmentAccounting accounting)
        {
            SaveAccountingCalls++;
            LastAccountingDraft = accounting;
            if (SaveAccountingException is not null)
                return Task.FromException(SaveAccountingException);
            var index = StoredAccounting.FindIndex(item => item.Id == accounting.Id);
            if (index >= 0)
                StoredAccounting[index] = accounting;
            else
                StoredAccounting.Add(accounting);
            return Task.CompletedTask;
        }

        public override Task DeleteAccountingAsync(int equipmentId, int accountingId) =>
            DeleteAccountingException is not null ? Task.FromException(DeleteAccountingException) : Task.CompletedTask;

        public override Task<IReadOnlyList<UpsServerLink>> GetUpsLinksAsync(int serverEquipmentId)
        {
            UpsRefreshCalls++;
            return Task.FromResult<IReadOnlyList<UpsServerLink>>([]);
        }

        public override Task<EquipmentActualInventory?> GetLastInventoryAsync(int equipmentId) =>
            Task.FromResult<EquipmentActualInventory?>(null);

        public override Task<int> GetNetworkAttachmentCountAsync(int equipmentId) => Task.FromResult(0);

        public override Task<IReadOnlyList<EquipmentNetworkNameHistory>> GetNetworkNameHistoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentNetworkNameHistory>>(
            [
                new() { EquipmentId = equipmentId, NetworkName = "old-host", ChangedAtUtc = new DateTime(2026, 11, 18), ChangedByName = "Тестовый сотрудник" },
            ]);

        public override Task<bool> CanUseAccountingAsync(int? personId) => Task.FromResult(AccountingAvailable);

        public Queue<TaskCompletionSource<Employee?>> OwnerReadCompletions { get; } = new();
        public bool DeferOwnerRead { get; set; }
        public Exception? OwnerReadException { get; set; }

        public override async Task<Employee?> GetCurrentUserWithRolesAsync()
        {
            if (OwnerReadException is not null)
                throw OwnerReadException;
            if (DeferOwnerRead)
                await OwnerReadCompletions.Dequeue().Task;
            var employee = new Employee { Id = 4, Name = "Тестов Администратор" };
            var grants = Roles.Select(roleId => new EmployeeRoleGrant
            {
                RoleId = roleId,
                PersonId = roleId is 102 or 103 ? 0 : -1,
            }).ToList();
            if (RoleForPersonResult && Roles.Contains(43))
            {
                grants.Add(new EmployeeRoleGrant { RoleId = 43, PersonId = 0 });
                grants.Add(new EmployeeRoleGrant { RoleId = 44, PersonId = 0 });
            }
            employee.SetRoleGrants(grants);
            return employee;
        }

        public override Task AttachNetworkAsync(int equipmentId)
        {
            AttachNetworkCalls++;
            return Task.CompletedTask;
        }

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

        public override Task AddAccountAsync(EquipmentAccount account)
        {
            AddAccountCalls++;
            return Task.CompletedTask;
        }

        public override Task DeleteSimAsync(int equipmentId)
        {
            DeleteSimCalls++;
            return Task.CompletedTask;
        }

        public override Task AddLocationAsync(
            EquipmentLocationPeriod location,
            EquipmentResponsiblePeriod? responsible = null) =>
            AddLocationAsync(location, responsible, false);

        public override Task AddLocationAsync(
            EquipmentLocationPeriod location,
            EquipmentResponsiblePeriod? responsible,
            bool expectedActiveResponsible)
        {
            LocationSaves.Add((location, responsible));
            return Task.CompletedTask;
        }

        public override Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible)
        {
            ResponsibleUpdated = responsible;
            if (ResponsibleException is not null)
                throw ResponsibleException;
            return Task.CompletedTask;
        }

        public override Task WithdrawResponsibleAsync(
            EquipmentResponsiblePeriod current,
            EquipmentResponsiblePeriod? next,
            EquipmentLocationPeriod? location)
        {
            WithdrawCalls.Add((current, next, location));
            return Task.CompletedTask;
        }

        public override Task<int?> GetConnectionEquipmentIdAsync(int phoneId, int? employeeId, int locationId) =>
            Task.FromResult<int?>(8);

        public override Task<IReadOnlyList<EquipmentLocationPeriod>> GetLocationsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentLocationPeriod>>(
                StoredLocations.Where(item => item.EquipmentId == equipmentId).ToList());

        public override Task<bool> HasCurrentResponsibleAsync(int equipmentId) => Task.FromResult(false);

        public override Task<IReadOnlyList<EquipmentPhoneLink>> GetPhonesAsync(int equipmentId)
        {
            GetPhonesCalls++;
            return Task.FromResult<IReadOnlyList<EquipmentPhoneLink>>(StoredPhones.ToList());
        }

        public override Task<int> GetHotelPhoneCountAsync(int equipmentId) => Task.FromResult(HotelPhoneCount);

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

        public override async Task<EquipmentPhoneSyncIntent> SaveWithPhonesAsync(
            EquipmentEntity equipment,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current)
        {
            await UpdateWithPhonesAsync(equipment, original, current);
            return new EquipmentPhoneSyncIntent($"save-{equipment.Id}", equipment.Id,
                EquipmentPhoneSyncOrigin.EquipmentSave, equipment.ChangedAtUtc, equipment.OwnerId,
                equipment.EquipmentTypeId, original.ToList(), current.ToList());
        }

        public override Task<PhoneSyncAttempt> SynchronizePhoneAttemptAsync(EquipmentPhoneSyncIntent intent)
        {
            SyncCalls++;
            if (SyncException is not null)
                throw SyncException;
            return Task.FromResult(CreateAttempt(intent, SyncResults));
        }

        public override Task<PhoneSyncAttempt> ConfigurePhoneEquipmentAttemptAsync(int equipmentId)
        {
            SyncCalls++;
            if (SyncException is not null)
                throw SyncException;
            var intent = new EquipmentPhoneSyncIntent($"configure-{equipmentId}", equipmentId,
                EquipmentPhoneSyncOrigin.ExplicitConfigure, default, 1, PhoneConfig ? 30 : 103, [], []);
            return Task.FromResult(CreateAttempt(intent, SyncResults));
        }

        public override Task<PhoneSyncAttempt> RetryPhoneSyncAttemptAsync(PhoneSyncAttempt attempt)
        {
            SyncCalls++;
            LastSyncOperations = attempt.Operations.Where(result => result.CanRetry).Select(result => result.Operation).ToArray();
            if (SyncException is not null)
                throw SyncException;
            return Task.FromResult(SyncResults is null ? attempt : CreateAttempt(attempt.Intent, SyncResults));
        }

        public override Task<PhoneSyncReconciliation> ReconcilePhoneSyncAttemptAsync(PhoneSyncAttempt attempt) =>
            Task.FromResult(new PhoneSyncReconciliation(attempt, null, [], false));

        private static PhoneSyncAttempt CreateAttempt(
            EquipmentPhoneSyncIntent intent, IReadOnlyList<PhoneSyncResult>? results) =>
            new(intent, (results ?? []).Select(result => new PhoneSyncOperationResult(intent.AttemptId,
                result.Operation, result.Success ? PhoneSyncPhase.Succeeded :
                result.IsUnknown ? PhoneSyncPhase.Unknown : PhoneSyncPhase.Failed, result.Error)).ToArray());

        public override Task<IReadOnlyList<PhoneSyncResult>> SynchronizePhoneOperationsAsync(
            int equipmentId,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current)
        {
            SyncCalls++;
            if (SyncException is not null)
                throw SyncException;
            return Task.FromResult(SyncResults ?? (IReadOnlyList<PhoneSyncResult>)[]);
        }

        public override Task<IReadOnlyList<PhoneSyncResult>> ConfigurePhoneEquipmentAsync(int equipmentId)
        {
            SyncCalls++;
            if (SyncException is not null)
                throw SyncException;
            return Task.FromResult(SyncResults ?? (IReadOnlyList<PhoneSyncResult>)[]);
        }

        public override Task<IReadOnlyList<PhoneSyncResult>> ExecutePhoneSyncOperationsAsync(
            int equipmentId,
            IReadOnlyCollection<EquipmentPhoneLink> original,
            IReadOnlyCollection<EquipmentPhoneLink> current,
            IEnumerable<PhoneSyncOperation> operations)
        {
            SyncCalls++;
            LastSyncOperations = operations.ToArray();
            if (SyncException is not null)
                throw SyncException;
            return Task.FromResult(SyncResults ?? (IReadOnlyList<PhoneSyncResult>)[]);
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

    private sealed class DeferredJSRuntime(IJSRuntime inner) : IJSRuntime
    {
        public string? DeferredIdentifier { get; set; }
        public string? DeferredArgument { get; set; }
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == DeferredIdentifier &&
                (DeferredArgument is null || Equals(args?.FirstOrDefault(), DeferredArgument)))
            {
                Requested.TrySetResult();
                await Completion.Task.WaitAsync(cancellationToken);
            }
            return await inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }
    }
}
