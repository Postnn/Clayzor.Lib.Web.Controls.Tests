using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет раздельные первичное и повторное восстановления защищённых полей.</summary>
public sealed class EquipmentProtectedRawRetryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProtectedRetryUsesFreshAccessAndOnlyThePendingCallback(bool sim, bool denyOnRetry)
    {
        await using var context = CreateContext(out var db);
        var services = context.Services;
        var backend = new AuthorizedFixture(db) { DenyOnRetry = denyOnRetry };
        services.AddSingleton<EquipmentFormService>(backend);
        services.AddSingleton(new MobileContractSelectDataSource(db));
        services.AddSingleton(new PersonalDataPersonSelectDataSource(db));
        services.AddScoped<ClayDraftService>();
        var provider = context.Render<MudDialogProvider>();
        var dialogService = services.GetRequiredService<IDialogService>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialCalls = 0;
        var retryCalls = 0;
        var writes = 0;
        var readiness = new List<EquipmentEditorReadiness>();
        var editor = new EquipmentEditorContext(7, 1, () => true)
        {
            RawRestore = () => { initialCalls++; return hold.Task; },
            PendingRawRestore = () => { retryCalls++; return retryHold.Task; },
            ReadinessChanged = state => { readiness.Add(state); return Task.CompletedTask; },
        };
        Task<EquipmentEditorSaveResult> Save()
        {
            writes++;
            return Task.FromResult(EquipmentEditorSaveResult.Commit());
        }
        var reference = sim
            ? await dialogService.ShowAsync<EquipmentSimDialog>("SIM", new DialogParameters<EquipmentSimDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.HasExisting, true },
                { dialog => dialog.OriginalVersion, "fixture-version" },
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            })
            : await dialogService.ShowAsync<EquipmentAccountDialog>("Учётная запись", new DialogParameters<EquipmentAccountDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.OriginalLogin, "fixture" },
                { dialog => dialog.Login, "fixture" },
                { dialog => dialog.OriginalVersion, "fixture-version" },
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            });
        try
        {
            provider.WaitForAssertion(() => Assert.Equal(1, initialCalls));
            Assert.True(Button(provider, "OK").HasAttribute("disabled"));
            var child = editor.Component!;
            await InvokeAsync(provider, child, "Apply");
            Assert.Equal(0, writes);
            hold.SetException(new InvalidOperationException("Pending"));
            provider.WaitForAssertion(() => Assert.Equal(2, backend.Reads));
            Assert.True(Button(provider, "OK").HasAttribute("disabled"));
            if (!denyOnRetry)
            {
                provider.WaitForAssertion(() => Assert.Equal(1, retryCalls));
                await InvokeAsync(provider, child, sim ? "InitializeReadinessAsync" : "LoadSecretsAndRawAsync");
                await InvokeAsync(provider, child, "Apply");
                Assert.Equal(0, writes);
                Assert.True(Button(provider, "OK").HasAttribute("disabled"));
                retryHold.SetResult();
            }
            provider.WaitForAssertion(() =>
                Assert.Equal(denyOnRetry ? EquipmentEditorReadiness.Failed : EquipmentEditorReadiness.Ready, readiness[^1]));
            Assert.Equal(2, backend.Reads);
            Assert.Equal(1, initialCalls);
            Assert.Equal(denyOnRetry ? 0 : 1, retryCalls);
            Assert.Equal(denyOnRetry, Button(provider, "OK").HasAttribute("disabled"));
            Assert.Equal(denyOnRetry ? EquipmentEditorReadiness.Failed : EquipmentEditorReadiness.Ready, readiness[^1]);
            Assert.False(reference.Result.IsCompleted);
            Assert.Equal(0, writes);
        }
        finally { hold.TrySetResult(); retryHold.TrySetResult(); }
    }

    [Fact]
    public async Task NestedUpsTestBlocksDirectAndButtonApplyAndSerializesRetry()
    {
        await using var context = CreateContext(out _);
        var provider = context.Render<MudDialogProvider>();
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var checks = 0;
        var reference = await context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<EquipmentUpsTestDialog>("Тест UPS", new DialogParameters<EquipmentUpsTestDialog>
            {
                { dialog => dialog.Date, new DateTime(2026, 11, 26) },
                { dialog => dialog.Time, new TimeSpan(9, 0, 0) },
                { dialog => dialog.RestoreRaw, () => ++calls == 1 ? hold.Task : retry.Task },
                { dialog => dialog.CanAcceptDraft, () => { checks++; return Task.FromResult(true); } },
            });
        var dialog = provider.FindComponent<EquipmentUpsTestDialog>();
        try
        {
            provider.WaitForAssertion(() => Assert.Equal(1, calls));
            Assert.True(Button(provider, "OK").HasAttribute("disabled"));
            await InvokeAsync(provider, dialog.Instance, "Apply");
            Assert.Equal(0, checks);
            Assert.False(reference.Result.IsCompleted);
            hold.SetResult(false);
            provider.WaitForAssertion(() => Assert.Equal(2, calls));
            Assert.DoesNotContain(provider.FindAll("button"), button => button.TextContent.Trim() == "Повторить восстановление");
            await InvokeAsync(provider, dialog.Instance, "RetryRawAsync");
            Assert.Equal(2, calls);
            retry.SetResult(true);
            provider.WaitForAssertion(() => Assert.False(Button(provider, "OK").HasAttribute("disabled")));
            await Button(provider, "OK").ClickAsync();
            Assert.False((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2)))!.Canceled);
            Assert.Equal(1, checks);
        }
        finally { hold.TrySetResult(false); retry.TrySetResult(false); }
    }

    private static Task InvokeAsync(IRenderedComponent<MudDialogProvider> renderer, ComponentBase target, string method) =>
        renderer.InvokeAsync(() => (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, null)!);

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<MudDialogProvider> dialog, string text) =>
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private static BunitContext CreateContext(out DbManager db)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddSingleton(new ClayFormWindowService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton<NavigationManager>(new FakeNavigationManager());
        db = new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Неожиданный SQL."));
        context.Services.AddSingleton(db);
        return context;
    }

    private sealed class AuthorizedFixture(DbManager db) : EquipmentFormService(db, new TestAuthenticationStateProvider())
    {
        internal bool DenyOnRetry { get; init; }
        internal int Reads { get; private set; }

        private void Reauthorize()
        {
            Reads++;
            if (DenyOnRetry && Reads > 1)
                throw new UnauthorizedAccessException("Доступ не подтверждён.");
        }

        public override Task<EquipmentAccount> GetAccountEditSnapshotAsync(int equipmentId, string login)
        {
            Reauthorize();
            return Task.FromResult(new EquipmentAccount { EquipmentId = equipmentId, Login = login, ExpectedVersion = "fixture-version" });
        }

        public override Task<EquipmentSimCard?> GetSimEditorSnapshotAsync(int equipmentId)
        {
            Reauthorize();
            return Task.FromResult<EquipmentSimCard?>(new EquipmentSimCard { EquipmentId = equipmentId, EditorVersion = "fixture-version" });
        }

        public override Task<(bool IsEligible, string Message)> GetPersonalDataAdvisoryAsync(
            int equipmentId, int? contractId, int? personId, string technicalTariffs, DateTime? businessToday) =>
            Task.FromResult((true, string.Empty));
    }
}
