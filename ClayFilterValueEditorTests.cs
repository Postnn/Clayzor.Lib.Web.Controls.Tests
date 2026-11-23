using Bunit;
using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Grid.ColumnTypes;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки редактора значения составного фильтра.</summary>
public sealed class ClayFilterValueEditorTests
{
    /// <summary>Активное текстовое поле использует placeholder вместо внутреннего label.</summary>
    [Fact]
    public async Task TextEditor_UsesCenteredPlaceholder()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var component = context.Render<ClayFilterValueEditor>(parameters => parameters
            .Add(editor => editor.Type, ColumnTypeRegistry.FromKind(ColumnType.Text))
            .Add(editor => editor.Operator, ColumnFilterOperator.Contains));

        var field = Assert.Single(component.FindComponents<MudTextField<string>>()).Instance;
        Assert.Equal("Значение", field.Placeholder);
        Assert.True(string.IsNullOrEmpty(field.Label));
    }

    /// <summary>До выбора колонки disabled-поле имеет тот же placeholder без съехавшего label.</summary>
    [Fact]
    public async Task EmptyExpression_UsesSamePlaceholder()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var component = context.Render<ClayFilterExpression>(parameters => parameters
            .Add(expression => expression.Node, new ColumnFilter())
            .Add(expression => expression.Columns, []));

        var field = Assert.Single(component.FindComponents<MudTextField<string>>()).Instance;
        Assert.Equal("Значение", field.Placeholder);
        Assert.True(string.IsNullOrEmpty(field.Label));
        Assert.True(field.Disabled);
    }
}
