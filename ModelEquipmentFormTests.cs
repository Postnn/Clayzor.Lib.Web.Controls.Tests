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
using System.Security.Claims;
using Clayzor.Lib.Entities;
using Microsoft.AspNetCore.Components.Authorization;
using static Clayzor.Lib.Web.Controls.Tests.ModelEquipmentWriteBoundaryTests;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Профильные тесты формы модели оборудования.</summary>
public sealed partial class ModelEquipmentFormTests
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
        fixture.Enqueue(Script.Scalar(1));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Old", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Duplicate");

        FindButton(page, "Применить").Click();

        page.WaitForAssertion(() => Assert.Contains("уже существует", page.Markup));
        Assert.Equal(0, fixture.WriteCount);
        Assert.Equal(7, fixture.LastParameters(SQLQueries.SELECT_МодельОборудования_EXISTS)["Id"]);
    }

    /// <summary>Применение новой модели загружает сохранённую запись, аудит и чистый snapshot.</summary>
    [Fact]
    public async Task ApplyNewModel_SavesNavigatesAndReloadsCreatedModel()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5", [
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
        ]);
        fixture.PrepareModelWrite(Script.Scalar(42));
        fixture.Enqueue(ModelRow(new EquipmentModel
        {
            Id = 42, EquipmentTypeId = 5, Name = "Created model", ChangedById = 4,
            ChangedAt = new DateTime(2026, 11, 17, 12, 30, 0),
        }), Roles(Роли.Специалист_по_инвентаризации_оборудования), Usage());
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("CPU:", page.Markup), TimeSpan.FromSeconds(3));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Created model"));
        await page.InvokeAsync(() => FindButton(page, "Применить").Click());

        page.WaitForAssertion(() =>
        {
            Assert.EndsWith("/ModelEquipmentForm?id=42", fixture.Navigation.Uri);
            Assert.Equal(42, CurrentModel(page).Id);
            Assert.Equal(4, CurrentModel(page).ChangedById);
            Assert.NotNull(CurrentModel(page).ChangedAt);
            Assert.False(IsModified(page.Instance));
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.WriteCount);
        Assert.Equal("Created model", fixture.LastParameters(SQLQueries.INSERT_МоделиОборудования)["Name"]);
        AssertBlocksOtherModelAttempts(fixture.Context);
    }

    /// <summary>SQL-ошибка сохраняет введённые данные и не запускает закрытие или success-path.</summary>
    [Fact]
    public async Task SaveSqlException_KeepsPageAndInputWithoutSuccessPath()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5)), Script.Rows(),
        ], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing", PartNumber = "PN" });
        fixture.PrepareModelWrite(Script.Error(SqlExceptionFactory.Create(50000, "trigger")));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Changed");

        await page.InvokeAsync(() => InvokeSave(page.Instance, true));

        Assert.Equal("Changed", CurrentModel(page).Name);
        Assert.False(IsSaving(page.Instance));
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier is "clayModelEquipmentForm.close" or "clayModelEquipmentForm.refreshOpener");
        Assert.Contains("Результат сохранения модели неизвестен", page.Markup);
    }

    /// <summary>Нулевой ID после INSERT не вызывает навигацию и обновление родителя.</summary>
    [Fact]
    public async Task InsertWithoutId_DoesNotNavigateOrRefreshOpener()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5", [
            Script.Rows(TypeRow(5, computer: 1)), Script.Rows(),
        ]);
        fixture.PrepareModelWrite(Script.Scalar(0));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("CPU:", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Created model");
        var originalUri = fixture.Navigation.Uri;

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal(originalUri, fixture.Navigation.Uri);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier == "clayModelEquipmentForm.refreshOpener");
        Assert.Contains("Результат сохранения модели неизвестен", page.Markup);
    }

    /// <summary>Disposal во время проверки дубликата отменяет последующий success-path.</summary>
    [Fact]
    public async Task DisposeDuringSave_DoesNotSaveOrInvokeJsAfterLateContinuation()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [
            Script.Rows(TypeRow(5)), Script.Rows(),
        ], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing", PartNumber = "PN" });
        var duplicateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDuplicate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(new ScriptEntry { Scalar = 0, Started = duplicateStarted, Release = releaseDuplicate });
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Changed");
        var saveTask = page.InvokeAsync(() => InvokeSave(page.Instance, true));
        await duplicateStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        page.Instance.Dispose();
        page.Dispose();
        releaseDuplicate.TrySetResult();
        await saveTask;

        Assert.Equal(0, fixture.WriteCount);
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
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Смена маршрута во время ожидающего чтения запрещает старой модели публикацию.</summary>
    [Fact]
    public async Task LatePreviousLoad_DoesNotOverwriteNewModel()
    {
        DapperColumnMapper.Initialize();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Started = started, Release = release,
                Rows = ModelRow(new EquipmentModel { Id = 4, Name = "Old" }).Rows },
            ModelRow(new EquipmentModel { Id = 5, Name = "New" }),
            Roles(Роли.Специалист_по_инвентаризации_оборудования), Usage(),
        ]));
        var db = CreateDb(new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()])));
        using var page = new ModelEquipmentForm();
        SetInjected(page, "Db", CreateDb(models));
        SetInjected(page, "Authentication", new ModelAuthentication());
        SetInjected(page, "EquipmentTypeSource", new EquipmentTypeSelectDataSource(db));
        SetInjected(page, "MemoryTypeSource", new MemoryTypeSelectDataSource(db));
        var loadVersion = PrivateMember("_loadVersion", page);
        var load = PrivateMethod("LoadDataAsync");
        loadVersion.SetValue(page, 1);
        var firstLoad = (Task)load.Invoke(page, [4, 0, 1, null, null, null])!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        loadVersion.SetValue(page, 2);
        var secondLoad = (Task)load.Invoke(page, [5, 0, 2, null, null, null])!;
        release.TrySetResult();
        await Task.WhenAll(firstLoad, secondLoad).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(5, CurrentModel(page).Id);
        Assert.Equal("New", CurrentModel(page).Name);
    }

    /// <summary>Переоткрытие модели не требуется, чтобы отказ отозванной роли сохранил текущий ввод.</summary>
    [Fact]
    public async Task RoleRevokedAfterOpening_KeepsInputAndDoesNotWrite()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()],
            new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing", PartNumber = "PN" });
        fixture.PrepareModelWrite(allowWrite: false);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup), TimeSpan.FromSeconds(3));
        page.FindComponents<ClayTextBox>()[0].Find("input").Input("Local name");

        await page.InvokeAsync(() => InvokeSave(page.Instance, true));

        Assert.Contains("право на сохранение не подтверждены", page.Markup);
        Assert.Equal("Local name", CurrentModel(page).Name);
        Assert.Equal(0, fixture.WriteCount);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
    }

    /// <summary>Активный шаблон пишет только новый текст при свежей роли инженера.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhoneTemplateForm_UpdatesTemplateOnlyWithFreshDutyRole(bool roleAllowed)
    {
        var original = new EquipmentModel { Id = 7, EquipmentTypeId = (int)ТипыОборудования.IP_телефон, Name = "Phone", PhoneTemplate = "old cfg" };
        var fixture = CreateFixture("http://localhost/Equipment/PhoneTemplateForm?id=7", [], original);
        fixture.Enqueue(Script.Rows());
        fixture.Enqueue(ModelRow(original), roleAllowed ? Roles(Роли.Дежурный_инженер) : Roles());
        if (roleAllowed)
            fixture.Enqueue(Script.Scalar(1));
        ConfigureTemplateModule(fixture.Context, "new cfg");
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.Contains("old cfg", page.Markup), TimeSpan.FromSeconds(3));
        await SignalTemplateEditAsync(page);

        await page.InvokeAsync(() => (Task)typeof(PhoneTemplateForm)
            .GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page.Instance, [true])!);

        if (!roleAllowed)
        {
            Assert.Equal(0, fixture.WriteCount);
            Assert.Contains("право на сохранение не подтверждены", page.Markup);
            Assert.Equal("new cfg", page.Find("textarea").TextContent);
            return;
        }

        Assert.Equal(1, fixture.WriteCount);
        Assert.Equal("new cfg", fixture.LastParameters(SQLQueries.UPDATE_МоделиОборудования_ШаблонТелефона)["PhoneTemplate"]);
        Assert.DoesNotContain("Name", fixture.LastParameters(SQLQueries.UPDATE_МоделиОборудования_ШаблонТелефона).Keys);
    }

    /// <summary>Шаблон в открытом редакторе остаётся доступен при конфликте позднего UPDATE.</summary>
    [Fact]
    public async Task PhoneTemplateForm_StaleUpdateKeepsInputAndStaysOpen()
    {
        var original = new EquipmentModel { Id = 7, EquipmentTypeId = (int)ТипыОборудования.IP_телефон, Name = "Phone", PhoneTemplate = "old cfg" };
        var fixture = CreateFixture("http://localhost/Equipment/PhoneTemplateForm?id=7", [], original);
        fixture.Enqueue(Script.Rows());
        fixture.Enqueue(ModelRow(original), Roles(Роли.Дежурный_инженер), Script.Scalar(0));
        ConfigureTemplateModule(fixture.Context, "local cfg");
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<PhoneTemplateForm>();
        page.WaitForAssertion(() => Assert.Contains("old cfg", page.Markup), TimeSpan.FromSeconds(3));
        await SignalTemplateEditAsync(page);

        await page.InvokeAsync(() => (Task)typeof(PhoneTemplateForm)
            .GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page.Instance, [true])!);

        Assert.Contains("были изменены либо удалены", page.Markup);
        Assert.Equal("local cfg", page.Find("textarea").TextContent);
        Assert.Equal(1, fixture.WriteCount);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations,
            call => call.Identifier is "clayModelEquipmentForm.close" or "clayModelEquipmentForm.refreshOpener");
    }

    /// <summary>Сохранённый неоднозначный результат блокирует новую запись в новом экземпляре.</summary>
    [Fact]
    public async Task RestoredUnknownModelWrite_BlocksRetry()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing" });
        SetupRestore(fixture.Context, "unknown");
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Результат сохранения модели неизвестен", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Local"));
        fixture.Enqueue(ModelRow(new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Local" }));
        await page.InvokeAsync(() => FindButton(page, "Прочитать состояние модели").Click());
        Assert.Contains("Состояние строки не доказывает исход", page.Markup);
        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal(0, fixture.WriteCount);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Восстановленный подтверждённый идентификатор завершает только журнал.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredConfirmedModelWrite_RetriesOnlyCleanup(bool receiptPresent)
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing" });
        SetupRestore(fixture.Context, "confirmed", receiptPresent ? new Dictionary<string, string>
        {
            ["receipt"] = """{"AttemptId":"fixture-attempt","Id":7,"Fingerprint":"fixture"}""",
        } : null);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Сохранение подтверждено", page.Markup));

        await page.InvokeAsync(() => FindButton(page, "Проверить журнал сохранения").Click());

        Assert.Equal(0, fixture.WriteCount);
        Assert.Equal(9L, Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]).Arguments[1]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Успешный DML не уничтожает ввод после отправки immutable модели.</summary>
    [Fact]
    public async Task ConfirmedModelWrite_PreservesLateInput()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing" });
        fixture.PrepareModelWrite(new ScriptEntry { Scalar = 1, Started = started, Release = release });
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Submitted"));

        var save = page.InvokeAsync(() => InvokeSave(page.Instance, true));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Late input"));
        }
        finally { release.TrySetResult(); }
        await save.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Submitted", fixture.LastParameters(SQLQueries.UPDATE_МоделиОборудования)["Name"]);
        Assert.Equal("Late input", CurrentModel(page).Name);
        Assert.Contains(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "model-input") &&
                call.Arguments[2] is string json && json.Contains("Late input", StringComparison.Ordinal));
        Assert.Equal(1, fixture.WriteCount);
        Assert.Contains("Сохранение подтверждено. Поздний ввод", page.Markup);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
    }

    /// <summary>Ошибка обновления родителя после коммита не объявляет запись несостоявшейся.</summary>
    [Fact]
    public async Task ConfirmedModelWrite_OpenerFailureDoesNotRepeatWrite()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing" });
        fixture.PrepareModelWrite();
        fixture.Context.JSInterop.SetupVoid("clayModelEquipmentForm.refreshOpener")
            .SetException(new JSDisconnectedException("fixture disconnect"));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Submitted"));
        await page.InvokeAsync(() => InvokeSave(page.Instance, false));
        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Contains("Сохранение подтверждено", page.Markup);
        Assert.Equal(1, fixture.WriteCount);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Потерянный ответ Begin останавливает модель до первой возможной записи.</summary>
    [Fact]
    public async Task ModelWriteWithoutDurableBeginAcknowledgement_DoesNotDispatch()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing" });
        fixture.PrepareModelWrite();
        fixture.Context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetException(new JSDisconnectedException("fixture lost begin"));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Private model text"));

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal(0, fixture.WriteCount);
        Assert.Contains("Запись не отправлена", page.Markup);
        var begin = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.DoesNotContain("Private model text", (string)begin.Arguments[1]!);
        AssertBlocksOtherModelAttempts(fixture.Context);
    }

    /// <summary>Точный результат ноль строк завершает попытку как доказанный конфликт.</summary>
    [Fact]
    public async Task ModelCasRejection_DoesNotBecomeUnknown()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Existing" });
        fixture.PrepareModelWrite(Script.Scalar(0));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Existing", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Local"));

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal(1, fixture.WriteCount);
        Assert.Equal("Local", CurrentModel(page).Name);
        Assert.Contains("были изменены либо удалены", page.Markup);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    /// <summary>Обычное событие ввода сохраняет Current и подлинный Original без текста шаблона.</summary>
    [Fact]
    public async Task EditingModel_PersistsScopedTypedDraftWithoutTemplate()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()],
            new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original", PhoneTemplate = "secret-template-fixture" });
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Original", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Edited"));

        page.WaitForAssertion(() =>
        {
            var call = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
            Assert.Equal("model-input", call.Arguments[1]);
            var json = Assert.IsType<string>(call.Arguments[2]);
            using var stored = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal("Original", stored.RootElement.GetProperty("Original").GetProperty("Fields").GetProperty("Name").GetString());
            Assert.Equal("Edited", stored.RootElement.GetProperty("Current").GetProperty("Name").GetString());
            Assert.DoesNotContain("secret-template-fixture", json);
        });
    }

    /// <summary>Совпадающий исходный снимок восстанавливается, конфликт сохраняет старый original без подмены fresh.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedDraftRestoresLocalInputAndRejectsFreshBaselineSubstitution(bool concurrentChange)
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()],
            new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = concurrentChange ? "Server changed" : "Original" });
        var edit = EditorDraftJson("Original", "Local restored");
        fixture.Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("model-fixture", true, 10, null, null, new Dictionary<string, string> { ["model-input"] = edit }));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Equal("Local restored", CurrentModel(page).Name));

        var baseline = PrivateMember("_writeBaseline", page.Instance).GetValue(page.Instance);
        if (concurrentChange)
        {
            Assert.Null(baseline);
            page.WaitForAssertion(() => Assert.Contains("Сохранён исходный снимок", page.Markup));
            await page.InvokeAsync(() => InvokeSave(page.Instance, false));
            Assert.Equal(0, fixture.WriteCount);
            Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        }
        else
        {
            Assert.NotNull(baseline);
            Assert.DoesNotContain("запись требует сверки", page.Markup);
        }
        var preservedOriginal = PrivateMember("_editorOriginal", page.Instance).GetValue(page.Instance)!;
        Assert.Contains("Original", System.Text.Json.JsonSerializer.Serialize(preservedOriginal));
        Assert.DoesNotContain("Server changed", System.Text.Json.JsonSerializer.Serialize(preservedOriginal));
    }

    /// <summary>Неполное восстановление raw сохраняет пользовательские данные и не отправляет DML.</summary>
    [Fact]
    public async Task PendingRawRestoration_BlocksDispatch()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original" });
        fixture.Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("model-fixture", true, 10, null, null,
                new Dictionary<string, string> { ["model-input"] = EditorDraftJson("Original", "Local") }));
        fixture.Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(10, true, false));
        fixture.Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], ["data:model.monitor:0"]));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Часть ввода ещё не восстановлена", page.Markup));

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal("Local", CurrentModel(page).Name);
        Assert.Equal(0, fixture.WriteCount);
        Assert.Equal("model-input", Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.applyRaw"]).Arguments[1]);
    }

    /// <summary>Новая и существующая карточки показывают scoped Failed, повторяя только pending.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ModelRawReadiness_FailedScopeUsesPendingOnly(bool existing, bool authorized)
    {
        var fixture = CreateFixture(existing ? "http://localhost/Equipment/ModelEquipmentForm?id=7"
            : "http://localhost/Equipment/ModelEquipmentForm?codetype=5",
            [Script.Rows(TypeRow(5)), Script.Rows()],
            existing ? new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original" } : null);
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("model-fixture", true, 10, null, null));
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(10, true, false));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], ["model-input"]));
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Повторить восстановление ввода", page.Markup));
        fixture.Enqueue(Roles(authorized ? Роли.Специалист_по_инвентаризации_оборудования : Роли.Дежурный_инженер));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], ["other-editor"]));

        await page.InvokeAsync(() => page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Повторить восстановление ввода").ClickAsync());

        if (authorized)
        {
            Assert.Equal("model-input", Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]).Arguments[1]);
            Assert.DoesNotContain("Повторить восстановление ввода", page.Markup);
        }
        else
        {
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]);
            Assert.Contains("Не удалось подтвердить сотрудника", page.Markup);
        }
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Готовность может измениться после открытия, но до Begin запись ещё не отправляется.</summary>
    [Fact]
    public async Task ModelWriteGate_FailedEditorScopeOffersRetryBeforeBegin()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original" });
        fixture.PrepareModelWrite();
        await using var context = fixture.Context;
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Original", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Submitted"));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], ["model-input"]));

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Contains("Повторить восстановление ввода", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Недоступная модель сохраняет Unknown и допускает только scoped повтор для своего сотрудника.</summary>
    [Fact]
    public async Task ReadOnlyModelRawRetry_PreservesUnknown()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7", [Script.Rows()]);
        fixture.Commands.Clear();
        fixture.Enqueue(Script.Rows());
        await using var context = fixture.Context;
        SetupRestore(context, "unknown", new Dictionary<string, string> { ["model-input"] = EditorDraftJson("Original", "Local") });
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(9, true, true));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(false, [], ["model-input"]));
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Повторить восстановление ввода", page.Markup));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));

        await page.InvokeAsync(() => page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Повторить восстановление ввода").ClickAsync());

        Assert.Equal("model-input", Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyPendingRaw"]).Arguments[1]);
        Assert.Contains("незавершённая попытка", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.retryStorage"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>После commit оставшийся raw означает поздний ввод, форму закрывать нельзя.</summary>
    [Fact]
    public async Task RawRevisionRemainingAfterCommit_LeavesFormOpen()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original" });
        fixture.PrepareModelWrite();
        fixture.Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(19, true, false));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Original", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Submitted"));

        await page.InvokeAsync(() => InvokeSave(page.Instance, true));

        Assert.Equal(1, fixture.WriteCount);
        Assert.Contains("Поздний ввод оставлен", page.Markup);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
        var intent = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var payload = System.Text.Json.JsonDocument.Parse((string)intent.Arguments[1]!);
        Assert.Equal("model-input", Assert.Single(payload.RootElement.GetProperty("acceptedScopes").EnumerateArray()).GetString());
    }

    /// <summary>Несовпадающая принятая typed-ревизия отклоняет попытку до DML.</summary>
    [Fact]
    public async Task BeginAcceptingDifferentTypedRevision_DoesNotDispatch()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original" });
        fixture.PrepareModelWrite();
        fixture.Context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(12);
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Original", page.Markup));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Entered later"));

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));

        Assert.Equal(0, fixture.WriteCount);
        Assert.Equal("Entered later", CurrentModel(page).Name);
        Assert.Contains("Ввод изменился до отправки", page.Markup);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Сырой черновик без исходной версии остаётся доступным без свежего baseline.</summary>
    [Fact]
    public async Task RawOnlyDraftDoesNotAcquireFreshBaseline()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Fresh" });
        fixture.Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("model-fixture", true, 10, null, null));
        fixture.Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(10, true, false));
        fixture.Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult(["data:model.name:0"], []));
        await using var _ = fixture.Context;
        var page = fixture.Context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("сверки исходной версии", page.Markup);
            Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        });

        Assert.Null(PrivateMember("_writeBaseline", page.Instance).GetValue(page.Instance));
        Assert.Null(PrivateMember("_editorOriginal", page.Instance).GetValue(page.Instance));
        await page.InvokeAsync(() => page.FindComponents<ClayTextBox>()[0].Find("input").Input("Still no original"));
        await page.InvokeAsync(() => InvokeSave(page.Instance, false));
        Assert.Equal(0, fixture.WriteCount);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    private static string EditorDraftJson(string original, string current)
    {
        var fields = new
        {
            EquipmentTypeId = 5, ResourceId = (int?)null, Name = original, PartNumber = string.Empty, Cpu = string.Empty,
            MonitorSize = 0d, MemoryTypeId = 0, MemoryBanks = 0, IsMultimedia = false, NetworkInterfaces = 0,
            NetworkName = string.Empty, Driver = string.Empty, LifeTime = string.Empty, Description = string.Empty,
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Original = new
            {
                Id = 7, ChangedAt = (DateTime?)null, Fields = fields,
                TemplateFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])),
            },
            Current = fields with { Name = current },
        });
    }

    /// <summary>Число создаёт внутреннюю ссылку на оборудование сохранённой модели по ID.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public async Task ModelUsageOpensInternalEquipmentListOnlyWhenItHasRows(int count)
    {
        var original = new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Имя с & <символами>" };
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows(TypeRow(5)), Script.Rows()], original, usageCount: count);
        await using var context = fixture.Context;
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() =>
        {
            Assert.Equal(7, CurrentModel(page).Id);
            Assert.True((bool)PrivateMember("_loaded", page.Instance).GetValue(page.Instance)!);
        });
        const string selector = "a[aria-label='Открыть список оборудования данной модели']";
        if (count == 0)
            Assert.Empty(page.FindAll(selector));
        else
        {
            var link = page.WaitForElement(selector);
            Assert.Equal("5", link.TextContent.Trim());
            Assert.Equal("http://localhost/?id=1001&modelId=7", link.GetAttribute("href"));
            Assert.Contains("из них списано 2", link.ParentElement!.TextContent);
        }
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Личный черновик доступен для копирования при отсутствии права записи или строки.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("raw-only")]
    [InlineData("sql-denied")]
    [InlineData("anonymous")]
    public async Task ReadOnlyRecoveryUsesVerifiedEmployeeAndPreservesOfficialState(string mode)
    {
        const string url = "http://localhost/Equipment/ModelEquipmentForm?id=7";
        var model = mode == "revoked"
            ? new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Official" }
            : null;
        var fixture = CreateFixture(url, [Script.Rows(TypeRow(5)), Script.Rows()], model);
        if (model is null)
        {
            fixture.Commands.Clear();
            fixture.Enqueue(mode == "sql-denied" ? Script.Error(SqlExceptionFactory.Create(229, "fixture denied")) : Script.Rows());
        }
        else
        {
            fixture.Commands.Clear();
            fixture.Enqueue(ModelRow(model), Roles(Роли.Дежурный_инженер), Usage());
        }

        await using var context = fixture.Context;
        if (mode == "anonymous")
            context.Services.AddSingleton<AuthenticationStateProvider, AnonymousModelAuthentication>();
        SetupRestore(context, "unknown", mode == "raw-only"
            ? null
            : new Dictionary<string, string> { ["model-input"] = EditorDraftJson("Original", "Local recovered") });
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(9, true, true));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult(["data:model.name:0"], ["path:INPUT:0"]));

        var page = context.Render<ModelEquipmentForm>();
        if (model is null)
            page.WaitForAssertion(() => Assert.Contains(mode == "sql-denied" ? "Недостаточно прав для чтения" : "не найдена", page.Markup));
        if (mode == "anonymous")
        {
            Assert.Empty(page.FindAll("[data-testid='model-readonly-recovery']"));
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
            return;
        }

        const string panel = "[data-testid='model-readonly-recovery']";
        page.WaitForAssertion(() => Assert.Contains("Часть исходного ввода осталась", page.Find(panel).TextContent));
        var register = Assert.Single(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Equal("7", register.Arguments[1]);
        Assert.Equal("4", register.Arguments[2]);
        var scope = Assert.IsType<string>(register.Arguments[4]);
        var keys = page.FindAll($"{scope} textarea[data-draft-key][readonly]")
            .Select(field => field.GetAttribute("data-draft-key")).ToArray();
        Assert.Equal(new[] { "model.type", "model.resource", "model.name", "model.pn", "model.cpu", "model.monitor",
            "model.memory-type", "model.memory-banks", "model.net", "model.net-name", "model.lifetime",
            "model.driver", "model.notes" }, keys);
        var localName = page.Find($"{panel} textarea[data-draft-key='model.name']");
        Assert.Equal(mode == "raw-only" ? string.Empty : "Local recovered", localName.TextContent);
        Assert.Equal("model-input", Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]).Arguments[1]);
        Assert.Contains("незавершённая попытка", page.Find(panel).TextContent);
        Assert.Equal(model?.Name, (PrivateMember("_model", page.Instance).GetValue(page.Instance) as EquipmentModel)?.Name);
        Assert.Null(PrivateMember("_writeBaseline", page.Instance).GetValue(page.Instance));

        await page.InvokeAsync(() => InvokeSave(page.Instance, false));
        await page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);

        Assert.Equal(0, fixture.WriteCount);
        Assert.DoesNotContain(context.JSInterop.Invocations, call =>
            call.Identifier is "clayDraftStore.beginOperationIntent" or "clayDraftStore.commitOperation" or
                "clayDraftStore.unknownOperation" or "clayDraftStore.saveTypedScope" or "clayDraftStore.discardScope");
    }

    /// <summary>Позднее восстановление прежнего маршрута не раскрывает черновик на новой карточке.</summary>
    [Fact]
    public async Task ReadOnlyRecoveryDropsLateRawCompletionAfterRouteChange()
    {
        const string url = "http://localhost/Equipment/ModelEquipmentForm?id=7";
        var fixture = CreateFixture(url, []);
        fixture.Commands.Clear();
        fixture.Enqueue(Script.Rows(), Script.Rows());
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", call => Equals(call.Arguments[1], "7"))
            .SetResult(new ClayDraftRestore("model-fixture", true, 10, null, null,
                new Dictionary<string, string> { ["model-input"] = EditorDraftJson("Original", "Previous route only") }));
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(9, true, false));
        var raw = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true);
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Previous route only", page.Markup));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count == 0)
            await Task.Delay(1, deadline.Token);
        var registration = Assert.Single(context.JSInterop.Invocations["clayDraftStore.register"]);
        var selector = Assert.IsType<string>(registration.Arguments[4]);
        Assert.Single(page.FindAll(selector));
        Assert.Contains("Previous route only", page.Markup);

        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/ModelEquipmentForm?id=8"));
        page.WaitForAssertion(() =>
        {
            Assert.Equal(8, page.Instance.Id);
            Assert.Empty(page.FindAll(selector));
            Assert.DoesNotContain("Previous route only", page.Markup);
        });
        raw.SetResult(new ClayDraftRawApplyResult(["data:model.name:0"], ["path:INPUT:0"]));
        await page.InvokeAsync(() => { });
        Assert.Empty(page.FindAll("[data-testid='model-readonly-recovery']"));
        Assert.DoesNotContain("Previous route only", page.Markup);
        Assert.Equal(0, fixture.WriteCount);
    }

    private sealed class AnonymousModelAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>Raw-only новая модель предлагает три исхода и подтверждает сброс исходной ревизии.</summary>
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    public async Task NewModelCancelHandlesRawOnlyInput(string choice)
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?codetype=5",
            [Script.Rows(TypeRow(5)), Script.Rows(), Script.Rows(TypeRow(5)), Script.Rows()]);
        fixture.Enqueue(Roles(Роли.Специалист_по_инвентаризации_оборудования));
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(12, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Equal(5, CurrentModel(page).EquipmentTypeId));
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Отмена").HasAttribute("disabled")));

        var cancelling = page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        const string reset = "Отменить мои изменения и начать заново";
        await PickModelCancelAsync(dialogs, choice == "continue" ? "Продолжить редактирование" :
            choice == "close" ? "Закрыть без сохранения" : reset, reset);
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.WriteCount);
        Assert.Equal(choice == "close" ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        if (choice == "continue")
        {
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Single(fixture.Commands);
            return;
        }
        var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal("model-input", replace.Arguments[1]);
        Assert.Equal(12L, replace.Arguments[2]);
        if (choice == "close")
            Assert.Null(replace.Arguments[3]);
        else
        {
            Assert.NotNull(replace.Arguments[3]);
            Assert.Empty(fixture.Commands);
            Assert.Equal(5, CurrentModel(page).EquipmentTypeId);
            Assert.False(IsModified(page.Instance));
        }
    }

    /// <summary>Reset применяет DB версию и поля после свежего доступа и CAS той же ревизии.</summary>
    [Theory]
    [InlineData("fresh")]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("late")]
    public async Task ExistingModelResetPublishesOnlyAcceptedFreshRevision(string mode)
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows(), Script.Rows(TypeRow(5)), Script.Rows()],
            new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Original", ChangedAt = new DateTime(2026, 11, 24) });
        var updated = new DateTime(2026, 11, 26);
        fixture.Enqueue(mode == "missing" ? Script.Rows() : ModelRow(new EquipmentModel
            { Id = 7, EquipmentTypeId = 5, Name = "Fresh", ChangedAt = updated }),
            mode == "denied" ? Roles(Роли.Дежурный_инженер) : Roles(Роли.Специалист_по_инвентаризации_оборудования), Usage());
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(21, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(mode != "late");
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Original", page.Markup));
        await page.InvokeAsync(() => { CurrentModel(page).Name = "Local"; });

        var cancelling = page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        await PickModelCancelAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        if (mode is "fresh" or "late")
        {
            var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(21L, replace.Arguments[2]);
            Assert.Contains("2026-11-26", Assert.IsType<string>(replace.Arguments[3]));
        }
        else
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(mode == "fresh" ? "Fresh" : "Local", CurrentModel(page).Name);
        Assert.Equal(mode == "fresh" ? updated : new DateTime(2026, 11, 24), CurrentModel(page).ChangedAt);
        Assert.Equal(mode != "fresh", IsModified(page.Instance));
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Закрытие не изменяет Unknown и не теряет незавершённый receipt.</summary>
    [Theory]
    [InlineData("unknown", true)]
    [InlineData("confirmed", false)]
    public async Task ModelExitKeepsAttemptAndKnownReceipt(string status, bool closes)
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows()], new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Official" });
        await using var context = fixture.Context;
        SetupRestore(context, status);
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid='model-save-journal']")));

        await page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());

        Assert.Equal(closes ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier is "clayDraftStore.replaceEditorScope" or
            "clayDraftStore.commitOperation" or "clayDraftStore.discardScope");
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Поздняя строка старой карточки не меняет новый маршрут и локальный scope.</summary>
    [Fact]
    public async Task ModelResetDropsDelayedReadAfterRouteChange()
    {
        var fixture = CreateFixture("http://localhost/Equipment/ModelEquipmentForm?id=7",
            [Script.Rows(TypeRow(5)), Script.Rows(), Script.Rows(TypeRow(5)), Script.Rows(), Script.Rows(TypeRow(5)), Script.Rows()],
            new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Official7" });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(new ScriptEntry { Started = started, Release = release,
            Rows = ModelRow(new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Late7" }).Rows },
            Roles(Роли.Специалист_по_инвентаризации_оборудования), Usage(),
            ModelRow(new EquipmentModel { Id = 8, EquipmentTypeId = 5, Name = "Official8" }),
            Roles(Роли.Специалист_по_инвентаризации_оборудования), Usage());
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(12, true, false));
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Official7", page.Markup));
        var cancelling = page.InvokeAsync(() => FindButton(page, "Отмена").ClickAsync());
        await PickModelCancelAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/ModelEquipmentForm?id=8"));
            page.WaitForAssertion(() => Assert.Equal(8, page.Instance.Id));
        }
        finally { release.TrySetResult(); }
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() => Assert.Contains("Official8", page.Markup));

        Assert.DoesNotContain("Late7", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    /// <summary>Внутренний переход учитывает выбор в диалоге и не переоткрывает исходный URL.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelNavigationOnlyProceedsAfterExplicitDiscard(bool discard)
    {
        const string uri = "http://localhost/Equipment/ModelEquipmentForm?id=7";
        var fixture = CreateFixture(uri, [Script.Rows(TypeRow(5)), Script.Rows()],
            new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Official" });
        await using var context = fixture.Context;
        var navigation = new ModelGuardNavigation(uri);
        context.Services.AddSingleton<NavigationManager>(navigation);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(3, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<ModelEquipmentForm>();
        page.WaitForAssertion(() => Assert.Contains("Official", page.Markup));
        var navigating = page.InvokeAsync(() =>
            navigation.CanNavigateAsync("http://localhost/Equipment/ModelEquipmentForm?id=8"));
        await PickModelCancelAsync(dialogs, discard ? "Закрыть без сохранения" : "Продолжить редактирование");

        Assert.Equal(discard, await navigating.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(discard ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        Assert.Equal(uri, navigation.Uri);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(0, fixture.WriteCount);
    }

    private sealed class ModelGuardNavigation : NavigationManager
    {
        public ModelGuardNavigation(string uri) => Initialize("http://localhost/", uri);

        protected override void SetNavigationLockState(bool value) { }

        public Task<bool> CanNavigateAsync(string target) =>
            NotifyLocationChangingAsync(target, null, false).AsTask();
    }

    private static Task PickModelCancelAsync(IRenderedComponent<MudDialogProvider> dialogs, string action, string? label = null)
    {
        dialogs.WaitForAssertion(() => Assert.Contains(action, dialogs.Markup));
        if (label is not null) Assert.Contains(label, dialogs.Markup);
        return dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == action).ClickAsync());
    }

    private static void AssertBlocksOtherModelAttempts(BunitContext context)
    {
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var intent = System.Text.Json.JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.True(intent.RootElement.GetProperty("blockOtherDraftOperations").GetBoolean());
    }

    private static Fixture CreateFixture(string uri, IEnumerable<ScriptEntry> entries, EquipmentModel? model = null, int usageCount = 0)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        SetupRestore(context);
        context.Services.AddScoped(_ => new ClayDraftService(new ModelJournalFixture(context.JSInterop.JSRuntime)));
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>("clayDraftStore.listRecoveryCandidates", _ => true).SetResult([]);
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 1, 1));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(11);
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(11, false, false));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true).SetResult(OperationJson("running"));
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries));
        var db = CreateDb(connection);
        var template = uri.Contains("PhoneTemplateForm", StringComparison.Ordinal);
        if (template)
        {
            context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(0, false, false));
            context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
                .SetResult(new ClayDraftRebaseState(0, []));
        }
        var commands = new Queue<ScriptEntry>();
        if (model is not null) commands.Enqueue(ModelRow(model));
        commands.Enqueue(Roles(Роли.Специалист_по_инвентаризации_оборудования, Роли.Дежурный_инженер));
        if (model is not null && !template) commands.Enqueue(usageCount > 0
            ? Script.Rows(Script.Row(("N", usageCount), ("Списанного", 2))) : Usage());
        var modelConnection = new ScriptedConnection(commands);
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
        context.Services.AddSingleton(CreateDb(modelConnection));
        context.Services.AddSingleton<AuthenticationStateProvider, ModelAuthentication>();
        context.Services.AddSingleton(new EquipmentTypeSelectDataSource(db));
        context.Services.AddSingleton(new MemoryTypeSelectDataSource(db));
        context.Services.AddSingleton(new ResourceSelectDataSource(CreateDb(new ScriptedConnection(new Queue<ScriptEntry>()))));
        context.Services.AddSingleton(new ClaySelectAdvancedSearchService(context.JSInterop.JSRuntime));
        return new Fixture(context, modelConnection, commands, model, navigation);
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

    private static ScriptEntry ModelRow(EquipmentModel model) => Script.Rows(Script.Row(
        ("КодМоделиОборудования", model.Id), ("КодТипаОборудования", model.EquipmentTypeId),
        ("МодельОборудования", model.Name), ("PN", model.PartNumber), ("CPU", model.Cpu),
        ("РазмерМонитора", model.MonitorSize), ("Мультимедиа", model.IsMultimedia),
        ("КодТипаПамяти", model.MemoryTypeId), ("БанковПамяти", model.MemoryBanks),
        ("Net", model.NetworkInterfaces), ("НазваниеNet", model.NetworkName),
        ("ШаблонТелефона", model.PhoneTemplate), ("Изменил", model.ChangedById), ("Изменено", model.ChangedAt)));

    private static ScriptEntry Usage() => Script.Rows(Script.Row(("N", 0), ("Списанного", 0)));

    private sealed record Fixture(BunitContext Context, ScriptedConnection Connection, Queue<ScriptEntry> Commands,
        EquipmentModel? Original, FakeNavigationManager Navigation)
    {
        internal int WriteCount => Connection.CommandLog.Count(IsWrite);

        internal Dictionary<string, object?> LastParameters(string sql) =>
            Connection.ParameterLog[Connection.CommandLog.FindLastIndex(command => command == sql)];

        internal void Enqueue(params ScriptEntry[] entries)
        {
            foreach (var entry in entries)
                Commands.Enqueue(entry);
        }

        internal void PrepareModelWrite(ScriptEntry? result = null, bool allowWrite = true)
        {
            Enqueue(Script.Scalar(0), Type(Original?.EquipmentTypeId ?? 5));
            if (Original is not null) Enqueue(ModelRow(Original));
            Enqueue(Script.Scalar(0), allowWrite ? Roles(Роли.Специалист_по_инвентаризации_оборудования) : Roles());
            if (allowWrite) Enqueue(result ?? Script.Scalar(1));
        }
    }

    private static void SetupRestore(BunitContext context, string? status = null, IReadOnlyDictionary<string, string>? scopes = null) =>
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("model-fixture", status is not null, 0, null,
                status is null ? null : OperationJson(status), scopes));

    private static string OperationJson(string status) =>
        $$"""{"version":1,"attemptId":"fixture-attempt","kind":"equipment-model.save","status":"{{status}}","revision":9,"data":"{\"Id\":7,\"Fingerprint\":\"fixture\"}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";

    private sealed class ModelJournalFixture(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "clayDraftStore.registerStream")
            {
                var restored = await inner.InvokeAsync<ClayDraftRestore?>("clayDraftStore.register", cancellationToken, args);
                if (restored is null) return default!;
                return (T)(object)new ClayDraftRestoreStream(restored.RegistrationId,
                    new TemplateTestStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(restored)));
            }
            var result = await inner.InvokeAsync<T>(identifier, cancellationToken, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || result is not string operationJson)
                return result;
            using var intent = System.Text.Json.JsonDocument.Parse((string)args![1]!);
            var returned = System.Text.Json.Nodes.JsonNode.Parse(operationJson)!;
            returned["attemptId"] = intent.RootElement.GetProperty("attemptId").GetString();
            returned["kind"] = intent.RootElement.GetProperty("kind").GetString();
            returned["data"] = intent.RootElement.GetProperty("data").GetString();
            if (intent.RootElement.GetProperty("acceptedScopes").GetArrayLength() > 0)
                returned["scopes"] = System.Text.Json.Nodes.JsonNode.Parse(
                    """[{"name":"model-input","typedRevision":11,"rawRevisions":{}}]""");
            if (intent.RootElement.GetProperty("kind").GetString() == "equipment-phone-template.save")
                returned["scopes"] = System.Text.Json.Nodes.JsonNode.Parse(
                    """[{"name":"template-input","typedRevision":11,"rawRevisions":{}}]""");
            return (T)(object)returned.ToJsonString();
        }
    }

    private static Task SignalTemplateEditAsync(IRenderedComponent<PhoneTemplateForm> page)
    {
        page.WaitForAssertion(() => Assert.False(page.Find("textarea").HasAttribute("readonly")));
        var generation = (string)typeof(PhoneTemplateForm)
            .GetField("_editorWatchGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!;
        return page.InvokeAsync(() => page.Instance.OnTemplateEditorChangedAsync(generation, 0));
    }

    private static void ConfigureTemplateModule(BunitContext context, string text)
    {
        var module = context.JSInterop.SetupModule("./js/phoneTemplateDraftText.js");
        module.Setup<PhoneTemplateForm.TemplateTextWire?>("readTemplateSnapshot", _ => true)
            .SetResult(new PhoneTemplateForm.TemplateTextWire(0, new TemplateTestStream(System.Text.Encoding.UTF8.GetBytes(text))));
        module.Setup<long?>("currentTemplateRevision", _ => true).SetResult(0);
    }

    private sealed class ModelAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, @"DOMAIN\fixture")], "fixture"))));
    }
}
