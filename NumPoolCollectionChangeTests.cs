using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет смысловое сравнение списка связей ТфОП с принятым состоянием.</summary>
public sealed class NumPoolCollectionChangeTests
{
    [Fact]
    public async Task EmployeeCollectionIgnoresDisplayAndOrderButTracksPrivateAndMembership()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<ClayErrorService>();
        context.Services.AddSingleton<ClayFormWindowService>();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", null,
            () => new ScriptedConnection(new Queue<ScriptEntry>(), emulateEquipmentIdentity: false)));
        context.Render<MudPopoverProvider>();
        NumPoolLink first = new(4, "Первый", 0, 0, null, null);
        NumPoolLink second = new(7, "Второй", 1, 0, null, null);
        var rendered = context.Render<NumPoolUseSection>(p => p
            .Add(x => x.Kind, NumPoolKind.Employee)
            .Add(x => x.Label, "Сотрудники")
            .Add(x => x.Links, [second, first with { Name = "Новое имя", ChangedBy = 19 }])
            .Add(x => x.BaselineLinks, [first, second])
            .Add(x => x.Editable, true)
            .Add(x => x.Private, true));
        const string marker = ".clay-field-change[data-clay-field-changed]";
        Assert.Empty(rendered.FindAll(marker));
        rendered.Render(p => p.Add(x => x.Links, [first, second with { Private = 0 }]));
        Assert.NotNull(rendered.Find($"{marker} > .clay-collection-field"));
        rendered.Render(p => p.Add(x => x.Links, [first]));
        Assert.Single(rendered.FindAll(marker));
        rendered.Render(p => p.Add(x => x.Links, [first, second]));
        Assert.Empty(rendered.FindAll(marker));
        rendered.Render(p => p.Add(x => x.Links, [first]).Add(x => x.Editable, false));
        Assert.Empty(rendered.FindAll(marker));
    }
}
