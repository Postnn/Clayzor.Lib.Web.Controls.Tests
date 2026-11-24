using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки immutable original и актуальной business date через actual Apply.</summary>
public sealed class Eq10EditorBaselineCalendarTests
{
    /// <summary>Fresh V2 не заменяет восстановленный original V1 и не перезаписывается.</summary>
    [Fact]
    public async Task LocationRestore_FreshV2KeepsV1AndRejectsWrite()
    {
        var v1 = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var key = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var original = new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 10, FromUtc = key, ChangedAtUtc = v1 };
        var source = new EquipmentLocationDialog
        {
            EquipmentId = 7,
            IsEdit = true,
            Original = EquipmentPeriodOriginal.FromLocation(original, true),
            LocationId = 10,
            Notes = "local-draft",
        };
        var sourceContext = new EquipmentEditorContext(7, 1, () => true) { Component = source };
        var json = sourceContext.CaptureDraftState();
        var fresh = new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 10, FromUtc = key, ChangedAtUtc = v1.AddSeconds(1), Notes = "fresh-v2",
        };
        await using var context = CreatePeriodContext();
        var provider = context.Render<MudDialogProvider>();
        var editorContext = new EquipmentEditorContext(7, 2, () => true) { DraftState = json };
        var saveCalls = 0;
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentLocationDialog>(
            "Location", new DialogParameters<EquipmentLocationDialog>
            {
                { dialog => dialog.EditorContext, editorContext },
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.IsEdit, true },
                { dialog => dialog.LocationId, 10 },
                { dialog => dialog.FreshOriginal, fresh },
                { dialog => dialog.FreshOriginalActive, true },
                { dialog => dialog.Now, new DateTime(2026, 11, 20, 10, 0, 0) },
                { dialog => dialog.Save, _ =>
                {
                    saveCalls++;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentLocationDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.Equal(v1, dialog.Instance.Original?.ChangedAtUtc);
        Assert.Equal("local-draft", dialog.Instance.Notes);
        Assert.Contains("изменён другим пользователем", dialog.Markup);
        Assert.Equal(0, saveCalls);
    }

    /// <summary>Missing/closed original Withdraw сохраняет ввод и не создаёт следующий период.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsibleWithdraw_MissingOrClosedOriginalDoesNotWrite(bool closed)
    {
        var key = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var original = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, EmployeeId = 4, FromUtc = key,
            ChangedAtUtc = key.AddMinutes(1),
        };
        var fresh = closed
            ? new EquipmentResponsiblePeriod
            {
                EquipmentId = 7, EmployeeId = 4, FromUtc = key, ToUtc = key.AddDays(1),
                ChangedAtUtc = original.ChangedAtUtc,
            }
            : null;
        await using var context = CreatePeriodContext();
        var provider = context.Render<MudDialogProvider>();
        var saveCalls = 0;
        var now = new DateTime(2026, 11, 20, 10, 0, 0);
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentResponsibleDialog>(
            "Responsible", new DialogParameters<EquipmentResponsibleDialog>
            {
                { dialog => dialog.Mode, EquipmentResponsibleDialogMode.Withdraw },
                { dialog => dialog.CurrentEmployeeId, 4 },
                { dialog => dialog.Original, EquipmentPeriodOriginal.FromResponsible(original, true) },
                { dialog => dialog.FreshOriginal, fresh },
                { dialog => dialog.FreshOriginalActive, !closed },
                { dialog => dialog.ToDate, now.Date },
                { dialog => dialog.ToTime, now.AddMinutes(-1).TimeOfDay },
                { dialog => dialog.Notes, "protected-draft" },
                { dialog => dialog.Now, now },
                { dialog => dialog.Save, _ =>
                {
                    saveCalls++;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentResponsibleDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Изъять").ClickAsync();

        Assert.Equal(0, saveCalls);
        Assert.Equal("protected-draft", dialog.Instance.Notes);
        Assert.Contains(closed ? "закрыт или изменён" : "удалён", dialog.Markup);
    }

    /// <summary>Нормальный edit передаёт родителю исходную версию с миллисекундами.</summary>
    [Fact]
    public async Task LocationApply_ReturnsOriginalBaseline()
    {
        var v1 = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var fresh = new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 10, FromUtc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
            ChangedAtUtc = v1,
        };
        await using var context = CreatePeriodContext();
        var provider = context.Render<MudDialogProvider>();
        EquipmentLocationDialogResult? saved = null;
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentLocationDialog>(
            "Location", new DialogParameters<EquipmentLocationDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.IsEdit, true },
                { dialog => dialog.LocationId, 10 },
                { dialog => dialog.FreshOriginal, fresh },
                { dialog => dialog.FreshOriginalActive, true },
                { dialog => dialog.Now, new DateTime(2026, 11, 20, 10, 0, 0) },
                { dialog => dialog.Save, result =>
                {
                    saved = result;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentLocationDialog>();
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.NotNull(saved);
        Assert.Equal(v1, saved.Original?.ChangedAtUtc);
        Assert.Equal(fresh.FromUtc, saved.Original?.FromUtc);
        Assert.Equal(10, saved.Original?.SubjectId);
    }

    /// <summary>Computer получает fresh Today до validation, не меняя identity daily snapshot.</summary>
    [Fact]
    public async Task ComputerApply_DeferredFreshTodayPrecedesValidationAndSave()
    {
        await using var context = CreatePeriodContext();
        var provider = context.Render<MudDialogProvider>();
        var opened = new DateTime(2026, 2, 27);
        var applied = opened.AddDays(1);
        var today = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        EquipmentComputerSnapshot? saved = null;
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentComputerDialog>(
            "Computer", new DialogParameters<EquipmentComputerDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.Date, opened },
                { dialog => dialog.ExpectedExisting, false },
                { dialog => dialog.Today, opened },
                { dialog => dialog.GetToday, () => today.Task },
                { dialog => dialog.MemoryBanks, 0 },
                { dialog => dialog.Configuration1Id, 1 },
                { dialog => dialog.Configuration1Date, applied },
                { dialog => dialog.Save, result =>
                {
                    saved = result;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentComputerDialog>();
        var click = dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        await Task.Delay(100);
        Assert.Null(saved);

        today.SetResult(applied);
        await click.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(saved);
        Assert.Equal(opened, saved.Date);
        Assert.False(saved.ExpectedExisting);
    }

    /// <summary>SIM service также валидируется по fresh Today после восстановления старого значения.</summary>
    [Fact]
    public async Task SimServiceApply_UsesFreshTodayBeforeValidation()
    {
        await using var context = CreatePeriodContext();
        var provider = context.Render<MudDialogProvider>();
        var opened = new DateTime(2026, 2, 27);
        var applied = opened.AddDays(1);
        EquipmentSimService? saved = null;
        await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentSimServiceDialog>(
            "SIM service", new DialogParameters<EquipmentSimServiceDialog>
            {
                { dialog => dialog.Value, new EquipmentSimService
                {
                    EquipmentId = 7, PhoneNumber = "79000000001", ServiceTypeId = "_TEST",
                    ServiceName = "Test", DateFrom = applied,
                } },
                { dialog => dialog.Today, opened },
                { dialog => dialog.GetToday, () => Task.FromResult(applied) },
                { dialog => dialog.Save, result =>
                {
                    saved = result;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var dialog = provider.FindComponent<EquipmentSimServiceDialog>();
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        Assert.NotNull(saved);
    }

    private static BunitContext CreatePeriodContext()
    {
        DapperColumnMapper.Initialize();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        context.Services.AddSingleton(new EmployeeSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new LocationSelectDataSource(CreateDb()));
        context.Services.AddSingleton(new EquipmentFormService(CreateDb(), new TestAuthenticationStateProvider()));
        return context;
    }

    private static DbManager CreateDb() =>
        new("Server=test", new FakeSqlErrorHandler(), () => new ScriptedConnection(new Queue<ScriptEntry>()));
}
