using Bunit;
using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>bUnit-проверки стартового состояния и восстановления главной Inventory.</summary>
public sealed class InventoryHomeTests
{
    [Fact]
    public async Task HomeWithoutId_LoadsRealMenuShapeAndShowsNeutralChoice()
    {
        await using var context = CreateContext();
        var cut = context.Render<Home>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-clay-navigation-menu]")));
        Assert.Equal(3, cut.FindAll(".clay-navigation-menu__root-button").Count);
        Assert.Single(cut.FindAll(".clay-navigation-menu__actions [aria-label='Тема оформления']"));
        Assert.Contains("Выберите запрос в меню", cut.Markup);
        Assert.DoesNotContain("clay-grid-root", cut.Markup);
        Assert.DoesNotContain("Не указан код запроса", cut.Markup);
    }

    [Fact]
    public async Task MenuLoadError_CanBeRetriedWithoutLosingPage()
    {
        var queue = new Queue<ScriptEntry>([Script.Error(new InvalidOperationException("temporary"))]);
        await using var context = CreateContext(queue);
        var cut = context.Render<Home>();
        cut.WaitForAssertion(() => Assert.Contains("Не удалось загрузить меню", cut.Markup));
        queue.Enqueue(Script.Rows(
            MenuRow(1001, 1, "Оборудование"), MenuRow(2001, 2, "Несоответствие"), MenuRow(3001, 3, "Отчёт")));

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-clay-navigation-menu]")));
        Assert.Contains("Выберите запрос в меню", cut.Markup);
    }

    private static BunitContext CreateContext(Queue<ScriptEntry>? queue = null)
    {
        queue ??= new Queue<ScriptEntry>([
            Script.Rows(MenuRow(1001, 1, "Оборудование"), MenuRow(2001, 2, "Несоответствие"), MenuRow(3001, 3, "Отчёт")),
        ]);
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Services.AddHttpContextAccessor();
        context.Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(
            new FakeAuthenticationStateProvider());
        context.Services.AddSingleton(new Clayzor.Lib.Web.Settings.ClayAppSettings());
        context.Services.AddScoped<Clayzor.Lib.Web.Controls.Themes.ClayThemeState>();
        context.Services.AddSingleton(new Clayzor.Lib.Web.Controls.ClayErrorService());
        context.Services.AddSingleton<ISqlErrorHandler, FakeSqlErrorHandler>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<NavigationManager>(new FakeNavigationManager());
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(),
            () => new ScriptedConnection(queue)));
        return context;
    }

    private static Dictionary<string, object?> MenuRow(int id, int type, string caption) => new()
    {
        ["КодЗапроса"] = id,
        ["Запрос"] = caption,
        ["ТипЗапроса"] = type,
        ["ФормаСписка"] = string.Empty,
        ["Пиктограмма"] = string.Empty,
        ["ПунктМенюОтчета"] = string.Empty,
        ["ФормаОтчета"] = string.Empty,
    };

    private sealed class FakeAuthenticationStateProvider
        : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        /// <inheritdoc />
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
