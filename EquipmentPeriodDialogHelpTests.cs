using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Themes;
using Clayzor.Lib.Web.DBSelect.Employees;
using Clayzor.Lib.Web.DBSelect.Locations;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет справку и общую тему дочерних редакторов периодов оборудования.</summary>
public sealed class EquipmentPeriodDialogHelpTests
{
    private const string WikiBase = "https://docs.example/wiki?topic=";

    [Theory]
    [InlineData(false, "LocationEquipment_form.aspx")]
    [InlineData(true, "ResponsibleEquipment_form.aspx")]
    public async Task ThemeAndHelpPreserveActiveEditorAndDraft(bool responsible, string legacyPage)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        AddThemeState(context);
        context.Services.AddSingleton(new ClayAppSettings { UriWikiHelp = WikiBase });
        context.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => throw new InvalidOperationException("Эта проверка не выполняет запросы."));
        context.Services.AddSingleton(new EmployeeSelectDataSource(db));
        context.Services.AddSingleton(new LocationSelectDataSource(db));
        context.Services.AddSingleton(new EquipmentFormService(db, new TestAuthenticationStateProvider()));
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var service = context.Services.GetRequiredService<IDialogService>();
        const string notes = "Текст до смены темы";
        var editor = new EquipmentEditorContext(7, 3, () => true);
        var typedWrites = new List<EquipmentEditorDraftChange>();
        var rawApplies = 0;
        var saves = 0;
        editor.DraftChanged = change => { typedWrites.Add(change); return Task.CompletedTask; };
        editor.RawRestore = () => { rawApplies++; return Task.CompletedTask; };
        Task<EquipmentEditorSaveResult> Save()
        {
            saves++;
            return Task.FromResult(EquipmentEditorSaveResult.Commit());
        }
        var reference = responsible
            ? await service.ShowAsync<EquipmentResponsibleDialog>("Ответственный", new DialogParameters<EquipmentResponsibleDialog>
            {
                { dialog => dialog.Mode, EquipmentResponsibleDialogMode.Issue },
                { dialog => dialog.Notes, notes },
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            })
            : await service.ShowAsync<EquipmentLocationDialog>("Расположение", new DialogParameters<EquipmentLocationDialog>
            {
                { dialog => dialog.Notes, notes },
                { dialog => dialog.EditorContext, editor },
                { dialog => dialog.Save, _ => Save() },
            });
        var actions = dialogs.FindComponent<ClayFormActions>();
        var group = actions.Find("[role='group']");
        Assert.Null(group.Closest("[data-draft-scope]"));
        Assert.Null(group.QuerySelector("[data-draft-key]"));
        var help = group.QuerySelector("a[aria-label='Документация']")!;
        Assert.Equal(WikiBase + "Inventory%2FEquipment%2F" + legacyPage, help.GetAttribute("href"));
        Assert.Equal("_blank", help.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", help.GetAttribute("rel"));

        const string changedNotes = "Несохранённая правка";
        await dialogs.Find("textarea").InputAsync(changedNotes);
        var originalState = editor.CaptureDraftState();
        Assert.NotNull(originalState);
        using var originalDraft = JsonDocument.Parse(originalState);
        Assert.Equal(changedNotes, originalDraft.RootElement.GetProperty("Notes").GetString());
        var typedCount = typedWrites.Count;
        var rawCount = rawApplies;

        await actions.Find("button[aria-label='Тема оформления']").ClickAsync(new MouseEventArgs());
        await popovers.WaitForElement("[data-clay-theme-option='modern:light']").ClickAsync(new MouseEventArgs());

        Assert.Equal(ClayThemePreference.ModernLight, context.Services.GetRequiredService<ClayThemeState>().Preference);
        Assert.Same(actions.Instance, dialogs.FindComponent<ClayFormActions>().Instance);
        Assert.Equal(changedNotes, dialogs.FindComponent<ClayTextArea>().Instance.Value);
        Assert.Equal(originalState, editor.CaptureDraftState());
        Assert.Equal(typedCount, typedWrites.Count);
        Assert.Equal(rawCount, rawApplies);
        Assert.False(reference.Result.IsCompleted);
        Assert.True(editor.IsCurrent);
        Assert.Equal(0, saves);
    }

    internal static async Task CheckHelpAndChangeThemeAsync(BunitContext context,
        IRenderedComponent<MudDialogProvider> dialogs, IRenderedComponent<MudPopoverProvider> popovers, string legacyPage)
    {
        var actions = dialogs.FindComponent<ClayFormActions>();
        var group = actions.Find("[role='group']");
        Assert.Null(group.Closest("[data-draft-scope]"));
        Assert.Null(group.QuerySelector("[data-draft-key]"));
        var help = group.QuerySelector("a[aria-label='Документация']")!;
        Assert.Equal(WikiBase + "Inventory%2FEquipment%2F" + legacyPage, help.GetAttribute("href"));
        Assert.Equal("_blank", help.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", help.GetAttribute("rel"));
        await actions.Find("button[aria-label='Тема оформления']").ClickAsync(new MouseEventArgs());
        await popovers.WaitForElement("[data-clay-theme-option='modern:light']").ClickAsync(new MouseEventArgs());
        Assert.Equal(ClayThemePreference.ModernLight, context.Services.GetRequiredService<ClayThemeState>().Preference);
        Assert.Same(actions.Instance, dialogs.FindComponent<ClayFormActions>().Instance);
    }

    internal static void AddAppearance(BunitContext context)
    {
        context.Services.AddSingleton(new ClayAppSettings { UriWikiHelp = WikiBase });
        AddThemeState(context);
    }

    internal static void AddThemeState(BunitContext context) =>
        context.Services.AddSingleton(new ClayThemeState(NullLogger<ClayThemeState>.Instance, null));
}
