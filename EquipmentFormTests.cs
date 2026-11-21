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
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<EquipmentLocationDialog>()));
        var dialog = provider.FindComponent<EquipmentLocationDialog>();
        Assert.True(dialog.Instance.RequireResponsible);

        await page.InvokeAsync(() => reference.Close(DialogResult.Ok(
            new EquipmentLocationDialogResult(55, DateTime.Now.AddMinutes(-1), null, "", null))));
        await pending;
        Assert.Empty(fixture.Service.LocationSaves);
        Assert.Contains("укажите ответственного сотрудника", page.Markup);

        dialogAdded = TrackNextDialog(context);
        pending = InvokeHandler(page, "OpenLocationDialogAsync", (object?)null);
        reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await page.InvokeAsync(() => reference.Close(DialogResult.Ok(
            new EquipmentLocationDialogResult(55, DateTime.Now.AddMinutes(-1), null, "", 4))));
        await pending;

        var save = Assert.Single(fixture.Service.LocationSaves);
        Assert.Equal(55, save.Location.LocationId);
        Assert.Equal(4, save.Responsible?.EmployeeId);
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

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
        WaitForSn(page, "CLAY_TEST_8");
        dialog.Close(DialogResult.Ok(new EquipmentAccountDialogResult("account-from-a", "", false, "")));
        await pending;

        Assert.Equal(0, fixture.Service.AddAccountCalls);
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
        var dialog = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.InvokeAsync(() => dialog.Close(DialogResult.Ok(new EquipmentResponsibleDialogResult(
            4, responsible.FromUtc, null, originalDate.AddDays(1), "new", null))));
        await pending;

        Assert.Equal(originalDate, responsible.ReturnDate);
        Assert.Equal("old", responsible.Notes);
        Assert.Equal(originalDate.AddDays(1), fixture.Service.ResponsibleUpdated?.ReturnDate);
        Assert.Contains("Ответственный не сохранён", page.Markup);
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
        fixture.Service.StoredAccounting.Add(new EquipmentAccounting
        {
            EquipmentId = 7, OwnerId = 20, TenantId = 30, ReceiptDocumentId = 40, WriteOffDocumentId = 50,
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
        Assert.DoesNotContain("Операции", page.Markup);
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
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Services.AddScoped<DbManager>(_ => CreateDb());
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton(new ClayFormWindowService(js));
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=7");
        var configuration = new ConfigurationManager
        {
            ["URI_user_form"] = "http://localhost/user",
            ["URI_model_form"] = "http://localhost/Equipment/ModelEquipmentForm",
            ["URI_equipment_form"] = "http://localhost/Equipment/EquipmentForm",
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
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("receipt", new DocumentSelectDataSource(CreateDb()));
        context.Services.AddKeyedSingleton<DocumentSelectDataSource>("writeoff", new DocumentSelectDataSource(CreateDb(writeOffEntries)));
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

        public override async Task<EquipmentEntity?> GetAsync(int id)
        {
            Requests.Add(id);
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
                LocationId = 55, SerialNumber = $"CLAY_TEST_{id}", Description = $"Copy{id}",
                MacAddress = "001122334455", WriteOffAtUtc = WriteOffDates.GetValueOrDefault(id),
                DocumentWriteOffId = WriteOffDocumentId,
                ChangedById = 4, ChangedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0), ChangedByName = "Тестов А.",
            };
        }

        /// <summary>Собирает пакетный результат из тех же тестовых данных, не обращаясь к БД.</summary>
        public override async Task<EquipmentFormReadResult> GetFormDataAsync(EquipmentFormReadRequest request)
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
                Accounts = [],
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
                NetworkNameHistory = request.IncludeNetworkName
                    ? await GetNetworkNameHistoryAsync(request.EquipmentId)
                    : []
            };
        }

        public override Task<IReadOnlyList<EquipmentResponsiblePeriod>> GetResponsiblesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentResponsiblePeriod>>(StoredResponsibles.ToList());

        public override Task<IReadOnlyList<EquipmentComputerSnapshot>> GetComputerHistoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentComputerSnapshot>>(StoredComputers.ToList());

        public override Task<IReadOnlyList<StandardConfiguration>> GetStandardConfigurationsAsync() =>
            Task.FromResult<IReadOnlyList<StandardConfiguration>>([]);

        public override Task<IReadOnlyList<EquipmentAccount>> GetAccountsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentAccount>>([]);

        public override Task<IReadOnlyList<EquipmentSimCard>> GetSimCardsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentSimCard>>([]);

        public override Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
            int equipmentId, bool showHistory, bool showRequired) =>
            Task.FromResult<IReadOnlyList<EquipmentSimService>>([]);

        public override Task<IReadOnlyList<SimServiceType>> GetSimServiceTypesAsync() =>
            Task.FromResult<IReadOnlyList<SimServiceType>>([]);

        public override Task<IReadOnlyList<EquipmentLicense>> GetLicensesAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentLicense>>([]);

        public override Task<IReadOnlyList<LicenseType>> GetLicenseTypesAsync() =>
            Task.FromResult<IReadOnlyList<LicenseType>>([]);

        public override Task<IReadOnlyList<EquipmentSoftwareInstallation>> GetSoftwareInstallationsAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentSoftwareInstallation>>(StoredSoftware.ToList());

        public override Task<IReadOnlyList<EquipmentAccounting>> GetAccountingAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentAccounting>>(StoredAccounting.ToList());

        public override Task<EquipmentRouterInfo?> GetRouterInfoAsync(int equipmentId) =>
            Task.FromResult<EquipmentRouterInfo?>(null);

        public override Task<IReadOnlyList<EquipmentRouterInventoryItem>> GetRouterInventoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentRouterInventoryItem>>([]);

        public Exception? UpsReadException { get; set; }
        public int UpsReadFailureAt { get; set; }
        public bool DeferUpsServers { get; set; }
        public TaskCompletionSource UpsServersRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpsServersCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IReadOnlyList<UpsServerLink>> GetUpsServersAsync(int upsEquipmentId)
        {
            if (UpsReadFailureAt == 1 && UpsReadException is not null)
                throw UpsReadException;
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

        public override Task<IReadOnlyList<UpsServerLink>> GetUpsLinksAsync(int serverEquipmentId) =>
            Task.FromResult<IReadOnlyList<UpsServerLink>>([]);

        public override Task<EquipmentActualInventory?> GetLastInventoryAsync(int equipmentId) =>
            Task.FromResult<EquipmentActualInventory?>(null);

        public override Task<int> GetNetworkAttachmentCountAsync(int equipmentId) => Task.FromResult(0);

        public override Task<IReadOnlyList<EquipmentNetworkNameHistory>> GetNetworkNameHistoryAsync(int equipmentId) =>
            Task.FromResult<IReadOnlyList<EquipmentNetworkNameHistory>>(
            [
                new() { EquipmentId = equipmentId, NetworkName = "old-host", ChangedAtUtc = new DateTime(2026, 11, 18), ChangedByName = "Тестовый сотрудник" },
            ]);

        public override Task<bool> CanUseAccountingAsync(int? personId) => Task.FromResult(AccountingAvailable);

        public override Task<bool> HasRoleAsync(int roleId) => Task.FromResult(Roles.Contains(roleId));

        public override Task<bool> HasRoleForPersonAsync(int roleId, int? personId) => Task.FromResult(RoleForPersonResult);

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
            EquipmentResponsiblePeriod? responsible = null)
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

        public override Task<IReadOnlyList<PhoneSyncResult>> ExecutePhoneSyncOperationsAsync(
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
