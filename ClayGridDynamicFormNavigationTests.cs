using Bunit;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Регрессии открытия форм динамического ClayGrid.</summary>
public sealed class ClayGridDynamicFormNavigationTests
{
    /// <summary>URL редактирования использует статический id и значение первичного ключа.</summary>
    [Fact]
    public void EditUrl_UsesStaticIdParameter()
    {
        var url = ClayGrid<IClayGridRow>.BuildDynamicEditUrl("http://localhost/Equipment/ModelEquipmentForm", "1240");

        Assert.Equal("http://localhost/Equipment/ModelEquipmentForm?id=1240", url);
        Assert.DoesNotContain("КодМоделиОборудования", url);
    }

    /// <summary>Существующие query-параметры формы сохраняются.</summary>
    [Fact]
    public void EditUrl_AppendsToExistingQuery()
    {
        var url = ClayGrid<IClayGridRow>.BuildDynamicEditUrl("/Equipment/ModelEquipmentForm?return=1", "1 240");

        Assert.Equal("/Equipment/ModelEquipmentForm?return=1&id=1%20240", url);
    }

    /// <summary>Сервис получает fake-параметры до вызова JS и открывает отдельное окно.</summary>
    [Fact]
    public async Task WindowService_GetsOptionsBeforeOpeningWindow()
    {
        using var ctx = new BunitContext();
        var open = ctx.JSInterop.Setup<bool>("clayFormWindow.open", _ => true);
        open.SetResult(true);
        var service = new FakeWindowService(ctx.JSInterop.JSRuntime);

        var opened = await service.OpenAsync("/Equipment/ModelEquipmentForm?id=1240", "ModelEquipmentForm");

        Assert.True(opened);
        Assert.Equal("ModelEquipmentForm", service.RequestedFormKey);
        var invocation = ctx.JSInterop.Invocations.Single(call => call.Identifier == "clayFormWindow.open");
        Assert.Equal("/Equipment/ModelEquipmentForm?id=1240", invocation.Arguments[0]);
        Assert.Equal("ModelEquipmentForm", invocation.Arguments[1]);
        var options = Assert.IsType<ClayFormWindowOptions>(invocation.Arguments[2]);
        Assert.Equal(900, options.Width);
    }

    /// <summary>Стандартная ширина popup соответствует значению 1100 в clayOpen.</summary>
    [Fact]
    public async Task WindowService_DefaultWidthMatchesClayOpenDefault()
    {
        using var ctx = new BunitContext();
        var open = ctx.JSInterop.Setup<bool>("clayFormWindow.open", _ => true);
        open.SetResult(true);
        var service = new ClayFormWindowService(ctx.JSInterop.JSRuntime);

        await service.OpenAsync("/Net/Net?id=15", "Net");

        var invocation = ctx.JSInterop.Invocations.Single(call => call.Identifier == "clayFormWindow.open");
        var options = Assert.IsType<ClayFormWindowOptions>(invocation.Arguments[2]);
        Assert.Equal(1100, options.Width);
    }

    private sealed class FakeWindowService : ClayFormWindowService
    {
        public FakeWindowService(Microsoft.JSInterop.IJSRuntime js) : base(js) { }
        public string? RequestedFormKey { get; private set; }

        public override Task<ClayFormWindowOptions> GetOptionsAsync(
            string formKey,
            CancellationToken cancellationToken = default)
        {
            RequestedFormKey = formKey;
            return Task.FromResult(new ClayFormWindowOptions { Width = 900 });
        }
    }
}
