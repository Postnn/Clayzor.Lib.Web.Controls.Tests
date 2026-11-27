using Bunit;
using Clayzor.Lib.Web.Controls.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Раскрываемые сведения ошибки операции в общей панели.</summary>
public sealed class ClayErrorOperationDetailsTests
{
    /// <summary>Исключение и его стек доступны только в раскрытой панели.</summary>
    [Fact]
    public void OperationExceptionIsRenderedOnlyInExpandedDetails()
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();
        var errors = new ClayErrorService();
        context.Services.AddSingleton(errors);
        var error = Assert.Throws<InvalidOperationException>(ThrowJournalFailure);
        errors.ReportOperationFailure("Не удалось проверить журнал", error);
        var panel = context.Render<ClayErrorBar>();

        Assert.Contains("Не удалось проверить журнал", panel.Markup);
        Assert.DoesNotContain("Journal check failed", panel.Markup);
        panel.Find(".clay-error-details").Click();
        var details = panel.Find(".clay-error-details-wrapper").TextContent;
        Assert.Contains("Journal check failed", details);
        Assert.Contains(nameof(ThrowJournalFailure), details);

        panel.InvokeAsync(() => errors.ReportOperationFailure("Повторная проверка", error)).GetAwaiter().GetResult();
        Assert.DoesNotContain("Journal check failed", panel.Markup);
    }

    /// <summary>Панель использует контрастный вариант текущей темы и доступный переключатель.</summary>
    [Fact]
    public void ErrorPanelUsesOutlinedSurfaceAndKeyboardAccessibleToggle()
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();
        var errors = new ClayErrorService();
        context.Services.AddSingleton(errors);
        errors.ReportOperationFailure("Ошибка операции", new InvalidOperationException("Failure"));
        var panel = context.Render<ClayErrorBar>();

        var alert = panel.Find(".clay-error-bar");
        Assert.Contains("mud-alert-outlined-error", alert.ClassList);
        Assert.DoesNotContain("mud-alert-filled-error", alert.ClassList);
        var toggle = panel.Find(".clay-error-details");
        Assert.Equal("BUTTON", toggle.TagName);
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));

        toggle.Click();
        Assert.Equal("true", panel.Find(".clay-error-details").GetAttribute("aria-expanded"));
    }

    private static void ThrowJournalFailure() => throw new InvalidOperationException("Journal check failed");
}
