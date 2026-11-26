using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Settings;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки срока возврата при редактировании периода расположения.</summary>
public sealed class EquipmentLocationDialogTests
{
    /// <summary>Проверяются обе календарные границы и отсутствие срока перед вызовом сохранения.</summary>
    [Theory]
    [InlineData(1, 30, false)]
    [InlineData(1, 31, true)]
    [InlineData(5, 28, true)]
    [InlineData(5, 29, false)]
    [InlineData(0, 0, true)]
    public async Task EditValidatesReturnDateBeforeSave(int month, int day, bool valid)
    {
        var now = new DateTime(2026, 2, 28, 19, 0, 0);
        var from = new DateTime(2026, 1, 31, 20, 30, 0);
        DateTime? returnDate = month == 0 ? null : new DateTime(2026, month, day);
        var fresh = new EquipmentLocationPeriod
        {
            EquipmentId = 7, LocationId = 14, FromUtc = from.AddHours(-3), ChangedAtUtc = from.AddHours(-2),
        };
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => new ScriptedConnection(
            new Queue<ScriptEntry>([Script.Rows(Script.Row(("Id", 14), ("Name", "Рабочее место")))])));
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new ClayAppSettings());
        EquipmentPeriodDialogHelpTests.AddThemeState(context);
        context.Services.AddSingleton(new EquipmentFormService(db));
        context.Services.AddSingleton(new LocationSelectDataSource(db));
        context.Services.AddSingleton(new EmployeeSelectDataSource(db));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var provider = context.Render<MudDialogProvider>();
        EquipmentLocationDialogResult? submitted = null;
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentLocationDialog>(
            "Расположение", new DialogParameters<EquipmentLocationDialog>
            {
                { dialog => dialog.EquipmentId, fresh.EquipmentId },
                { dialog => dialog.LocationId, fresh.LocationId },
                { dialog => dialog.IsEdit, true },
                { dialog => dialog.Now, now },
                { dialog => dialog.FromDate, from.Date },
                { dialog => dialog.FromTime, from.TimeOfDay },
                { dialog => dialog.FreshOriginal, fresh },
                { dialog => dialog.FreshOriginalActive, true },
                { dialog => dialog.ReturnDate, returnDate },
                { dialog => dialog.Notes, "Текущий черновик" },
                { dialog => dialog.Save, result =>
                {
                    submitted = result;
                    return Task.FromResult(EquipmentEditorSaveResult.Commit());
                } },
            });
        var form = provider.FindComponent<EquipmentLocationDialog>();

        await form.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();

        if (valid)
        {
            Assert.NotNull(submitted);
            Assert.Equal(returnDate, submitted.ReturnDate);
            Assert.Equal(from, submitted.FromLocal);
            Assert.Equal(fresh.FromUtc, submitted.Original!.FromUtc);
            Assert.False((await reference.Result)!.Canceled);
        }
        else
        {
            Assert.Null(submitted);
            Assert.Contains("Срок возврата должен быть не раньше даты начала и не позднее трёх месяцев.", form.Markup);
            Assert.Equal("Текущий черновик", form.Instance.Notes);
            await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        }
    }
}
