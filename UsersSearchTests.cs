using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Users.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Проверяет передачу начального поискового текста из URL в форму сотрудников.
/// </summary>
public sealed class UsersSearchTests
{
    /// <summary>
    /// Параметр search отображается в поле и применяется уже к первой серверной загрузке.
    /// </summary>
    [Fact]
    public async Task InitialSearch_AppliesToFieldAndFirstSqlLoad()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Scalar = 1 },
            Script.Rows(Script.Row(("Id", 3), ("Name", "Примеров Наблюдатель"), ("NameEn", "Prim Observer"))),
        ]));
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddMudExtensions();
        var errorService = new ClayErrorService();
        ctx.Services.AddSingleton<ClayErrorService>(errorService);
        ctx.Services.AddSingleton<ISqlErrorHandler>(errorService);
        ctx.Services.AddSingleton(new ClayAppSettings());
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton(new DbManager("Server=test", errorService, () => connection));
        var navigation = new FakeNavigationManager();
        navigation.NavigateTo("http://localhost/search?return=1&requestId=request-1&search=Примеров");
        ctx.Services.AddSingleton<NavigationManager>(navigation);

        var page = ctx.Render<Search>();

        page.WaitForAssertion(() => Assert.Equal(2, connection.CommandLog.Count));
        var searchField = page.FindComponents<MudTextField<string>>().Single();
        Assert.Equal("Примеров", searchField.Find("input").GetAttribute("value"));
        Assert.All(connection.CommandLog, sql => Assert.Contains("Name LIKE @search", sql));
        Assert.Contains("Примеров Наблюдатель", page.Markup);
    }
}
