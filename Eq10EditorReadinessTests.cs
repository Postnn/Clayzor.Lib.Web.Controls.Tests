using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Deferred readiness, scoped raw и guarded advisory на фактических редакторах.</summary>
public sealed class Eq10EditorReadinessTests
{
    /// <summary>Отказ доступа блокирует восстановление примечания до успешного повторного чтения.</summary>
    [Fact]
    public async Task AccountDeniedThenRetry_RestoresOrdinaryRawOnlyAfterFreshRead()
    {
        var service = new DeferredEquipmentService();
        var denied = NewRead<string>();
        service.AccountReads.Enqueue(denied);
        await using var context = CreateContext(service);
        var provider = context.Render<MudDialogProvider>();
        IRenderedComponent<EquipmentAccountDialog>? dialog = null;
        var rawCalls = 0;
        var saveCalls = 0;
        var failed = NewRead();
        var ready = NewRead();
        var typedStates = new List<string>();
        var editorContext = new EquipmentEditorContext(7, 1, () => true)
        {
            DraftChanged = change =>
            {
                typedStates.Add(change.State);
                return Task.CompletedTask;
            },
            RawRestore = async () =>
            {
                rawCalls++;
                var notes = dialog!.Find("input[data-draft-key='account.notes']");
                if (!notes.HasAttribute("disabled"))
                    await notes.InputAsync("raw-test-note");
            },
            ReadinessChanged = readiness =>
            {
                if (readiness == EquipmentEditorReadiness.Failed)
                    failed.TrySetResult();
                if (readiness == EquipmentEditorReadiness.Ready)
                    ready.TrySetResult();
                return Task.CompletedTask;
            },
        };
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentAccountDialog>(
            "Account", new DialogParameters<EquipmentAccountDialog>
            {
                { component => component.EditorContext, editorContext },
                { component => component.EquipmentId, 7 },
                { component => component.OriginalLogin, "account-a" },
                { component => component.Login, "account-a" },
                { component => component.Save, _ =>
                {
                    saveCalls++;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        dialog = provider.FindComponent<EquipmentAccountDialog>();
        await service.AccountStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(dialog.Instance.Password);
        Assert.Equal(0, rawCalls);

        denied.SetException(new UnauthorizedAccessException("denied-test"));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dialog.WaitForAssertion(() => Assert.True(dialog.Find("input[type='password']").HasAttribute("disabled")));
        Assert.Empty(dialog.Instance.Password);
        Assert.Equal(0, rawCalls);
        Assert.Equal(0, saveCalls);
        Assert.All(typedStates, state => Assert.DoesNotContain("Password", state, StringComparison.OrdinalIgnoreCase));

        var allowed = NewRead<string>();
        service.AccountReads.Enqueue(allowed);
        var retryClick = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Повторить").ClickAsync();
        allowed.SetResult("db-test-value");
        await retryClick.WaitAsync(TimeSpan.FromSeconds(2));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dialog.WaitForAssertion(() =>
        {
            Assert.Equal("db-test-value", dialog.Instance.Password);
            Assert.Equal("raw-test-note", dialog.Instance.Notes);
            Assert.False(dialog.Find("input[type='password']").HasAttribute("data-draft-key"));
        });
        Assert.Equal(1, rawCalls);
        Assert.All(service.AccountEquipmentIds, id => Assert.Equal(7, id));

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        Assert.Equal(1, saveCalls);
    }

    /// <summary>SIM restored ContractId/Pdn/source не зависят от порядка secret и contract completion.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SimRestore_BothCompletionOrdersPreserveContractAndPerson(bool secretsFirst)
    {
        var service = new DeferredEquipmentService();
        var contractStarted = NewRead();
        var contractRelease = NewRead();
        var contractConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry
            {
                Started = contractStarted,
                Release = contractRelease,
                Rows = [Script.Row(("КодДокумента", 11), ("ДоговорНазвание", "restored-contract"), ("ТипЛицаЗаказчика", 7))],
            },
        ]));
        var personalDataSource = new PersonalDataPersonSelectDataSource(CreateDb());
        await using var context = CreateContext(service, contractConnection, personalDataSource);
        var provider = context.Render<MudDialogProvider>();
        var source = new EquipmentSimDialog
        {
            EquipmentId = 7,
            HasExisting = true,
            OriginalVersion = "fixture-sim-version",
            PhoneNumber = "79000000001",
            ContractId = 11,
            PersonalDataPersonId = 99,
        };
        var sourceContext = new EquipmentEditorContext(7, 1, () => true) { Component = source };
        var json = sourceContext.CaptureDraftState();
        Assert.DoesNotContain("Secrets", json, StringComparison.OrdinalIgnoreCase);
        var rawCalls = 0;
        var saveCalls = 0;
        var ready = NewRead();
        var editorContext = new EquipmentEditorContext(7, 2, () => true)
        {
            DraftState = json,
            RawRestore = () =>
            {
                rawCalls++;
                provider.FindComponent<EquipmentSimDialog>().Instance.PhoneNumber = "79000000002";
                return Task.CompletedTask;
            },
            ReadinessChanged = readiness =>
            {
                if (readiness == EquipmentEditorReadiness.Ready)
                    ready.TrySetResult();
                return Task.CompletedTask;
            },
        };
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentSimDialog>(
            "SIM", new DialogParameters<EquipmentSimDialog>
            {
                { component => component.EditorContext, editorContext },
                { component => component.EquipmentId, 7 },
                { component => component.HasExisting, true },
                { component => component.PhoneNumber, "79000000001" },
                { component => component.ContractId, 10 },
                { component => component.PersonalDataPersonId, null },
                { component => component.Save, _ =>
                {
                    saveCalls++;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentSimDialog>();
        await service.SimStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await contractStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        if (secretsFirst)
            service.SimRead.SetResult(new EquipmentSimSecrets { Pin = "db-test-pin" });
        else
            contractRelease.SetResult();
        await Task.Delay(100);
        Assert.Equal(0, rawCalls);
        Assert.Equal(99, dialog.Instance.PersonalDataPersonId);

        if (secretsFirst)
            contractRelease.SetResult();
        else
            service.SimRead.SetResult(new EquipmentSimSecrets { Pin = "db-test-pin" });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));

        dialog.WaitForAssertion(() =>
        {
            Assert.Equal("db-test-pin", dialog.Instance.Secrets.Pin);
            Assert.Equal("79000000002", dialog.Instance.PhoneNumber);
            Assert.All(dialog.FindAll("input[type='password']"), input => Assert.False(input.HasAttribute("data-draft-key")));
        });
        Assert.Equal(11, dialog.Instance.ContractId);
        Assert.Equal(99, dialog.Instance.PersonalDataPersonId);
        Assert.Equal(11, personalDataSource.ContractId);
        Assert.Equal(7, personalDataSource.CustomerType);
        Assert.Equal(1, rawCalls);
        Assert.Equal(0, saveCalls);
    }

    /// <summary>Deferred initial advisory сам отрисовывает результат без следующего действия пользователя.</summary>
    [Fact]
    public async Task SimInitialAdvisory_CompletionRendersWarning()
    {
        var service = new DeferredEquipmentService { Advisory = NewRead<(bool IsEligible, string Message)>() };
        await using var context = CreateContext(service);
        var provider = context.Render<MudDialogProvider>();
        await ShowNewSimAsync(context, 99);
        var dialog = provider.FindComponent<EquipmentSimDialog>();
        await service.AdvisoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("advisory-test-visible", dialog.Markup);
        var personSelect = dialog.FindComponent<ClaySelect<PersonLookup, int?>>();
        await dialog.InvokeAsync(() => personSelect.Instance.SelectedItemChanged.InvokeAsync(
            new PersonLookup { Id = 99, Name = "programmatic-same-id" }));

        service.Advisory.SetResult((false, "advisory-test-visible"));

        dialog.WaitForAssertion(() => Assert.Contains("advisory-test-visible", dialog.Markup));
        Assert.Equal(99, dialog.Instance.PersonalDataPersonId);
    }

    /// <summary>Старая ошибка Fill после ручного выбора не заменяет текущий ID и advisory.</summary>
    [Fact]
    public async Task SimFillFault_AfterManualSelectionIsIgnored()
    {
        var service = new DeferredEquipmentService { EmployeePerson = NewRead<int?>() };
        await using var context = CreateContext(service);
        var provider = context.Render<MudDialogProvider>();
        var ready = NewRead();
        var editorContext = new EquipmentEditorContext(7, 1, () => true)
        {
            ReadinessChanged = readiness =>
            {
                if (readiness == EquipmentEditorReadiness.Ready)
                    ready.TrySetResult();
                return Task.CompletedTask;
            },
        };
        await ShowNewSimAsync(context, null, editorContext, currentEmployeeId: 4);
        var dialog = provider.FindComponent<EquipmentSimDialog>();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var fillClick = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Заполнить").ClickAsync();
        await service.FillStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var personSelect = dialog.FindComponent<ClaySelect<PersonLookup, int?>>();
        await dialog.InvokeAsync(() =>
        {
            dialog.Instance.PersonalDataPersonId = 77;
            return personSelect.Instance.SelectedItemChanged.InvokeAsync(new PersonLookup { Id = 77, Name = "manual-selection" });
        });
        service.EmployeePerson.SetException(new InvalidOperationException("stale-fill-error"));
        await fillClick.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(77, dialog.Instance.PersonalDataPersonId);
        Assert.DoesNotContain("stale-fill-error", dialog.Markup);
    }

    private static Task<IDialogReference> ShowNewSimAsync(
        BunitContext context,
        int? personId,
        EquipmentEditorContext? editorContext = null,
        int? currentEmployeeId = null) =>
        context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentSimDialog>(
            "SIM", new DialogParameters<EquipmentSimDialog>
            {
                { component => component.EditorContext, editorContext },
                { component => component.EquipmentId, 7 },
                { component => component.HasExisting, false },
                { component => component.PhoneNumber, "79000000001" },
                { component => component.ContractId, 10 },
                { component => component.PersonalDataPersonId, personId },
                { component => component.CurrentEmployeeId, currentEmployeeId },
            });

    private static BunitContext CreateContext(
        DeferredEquipmentService service,
        ScriptedConnection? contractConnection = null,
        PersonalDataPersonSelectDataSource? personalDataSource = null)
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton<EquipmentFormService>(service);
        context.Services.AddSingleton(new MobileContractSelectDataSource(contractConnection is null
            ? CreateDb()
            : new DbManager("Server=test", new FakeSqlErrorHandler(), () => contractConnection)));
        context.Services.AddSingleton(personalDataSource ?? new PersonalDataPersonSelectDataSource(CreateDb()));
        return context;
    }

    private static DbManager CreateDb() =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>()));

    private static TaskCompletionSource NewRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewRead<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DeferredEquipmentService : EquipmentFormService
    {
        internal DeferredEquipmentService()
            : base(CreateDb(), new TestAuthenticationStateProvider())
        {
        }

        internal Queue<TaskCompletionSource<string>> AccountReads { get; } = new();
        internal TaskCompletionSource AccountStarted { get; } = NewRead();
        internal List<int> AccountEquipmentIds { get; } = [];
        internal TaskCompletionSource<EquipmentSimSecrets> SimRead { get; } = NewRead<EquipmentSimSecrets>();
        internal TaskCompletionSource SimStarted { get; } = NewRead();
        internal TaskCompletionSource<(bool IsEligible, string Message)>? Advisory { get; init; }
        internal TaskCompletionSource AdvisoryStarted { get; } = NewRead();
        internal TaskCompletionSource<int?>? EmployeePerson { get; init; }
        internal TaskCompletionSource FillStarted { get; } = NewRead();

        public override async Task<EquipmentAccount> GetAccountEditSnapshotAsync(int equipmentId, string login)
        {
            AccountEquipmentIds.Add(equipmentId);
            AccountStarted.TrySetResult();
            return new EquipmentAccount
            {
                EquipmentId = equipmentId, Login = login,
                Password = await AccountReads.Dequeue().Task, ExpectedVersion = "fixture-version",
            };
        }

        public override async Task<EquipmentSimCard?> GetSimEditorSnapshotAsync(int equipmentId)
        {
            SimStarted.TrySetResult();
            return new EquipmentSimCard
            {
                EquipmentId = equipmentId, Secrets = await SimRead.Task, EditorVersion = "fixture-sim-version",
            };
        }

        public override Task<(bool IsEligible, string Message)> GetPersonalDataAdvisoryAsync(
            int equipmentId, int? contractId, int? personId, string technicalTariffs) =>
            GetPersonalDataAdvisoryAsync(equipmentId, contractId, personId, technicalTariffs, null);

        public override Task<(bool IsEligible, string Message)> GetPersonalDataAdvisoryAsync(
            int equipmentId, int? contractId, int? personId, string technicalTariffs, DateTime? businessToday)
        {
            AdvisoryStarted.TrySetResult();
            return Advisory?.Task ?? Task.FromResult<(bool IsEligible, string Message)>((true, string.Empty));
        }

        public override Task<int?> GetEmployeePersonIdAsync(int employeeId)
        {
            FillStarted.TrySetResult();
            return EmployeePerson?.Task ?? Task.FromResult<int?>(null);
        }
    }
}
