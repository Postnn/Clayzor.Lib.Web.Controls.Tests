using Clayzor.Lib.Web.Controls.Components.Filter;
using Clayzor.Lib.Web.Controls.Components.Grid;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Components.Tree.Helpers;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты построителя списка фильтруемых колонок дерева <see cref="ClayTreeFilterColumnBuilder"/>.
/// </summary>
public class ClayTreeFilterColumnBuilderTests
{
    /// <summary>Null-список колонок → пустой результат.</summary>
    [Fact]
    public void Build_NullColumns_ReturnsEmpty()
    {
        var result = ClayTreeFilterColumnBuilder.Build(null, []);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    /// <summary>Пустой список колонок → пустой результат.</summary>
    [Fact]
    public void Build_EmptyColumns_ReturnsEmpty()
    {
        var result = ClayTreeFilterColumnBuilder.Build([], []);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    /// <summary>Одна текстовая колонка → один ClayFilterColumnInfo с типом Text.</summary>
    [Fact]
    public void Build_SingleTextColumn_ReturnsOneEntry()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Name", DisplayName = "Имя" },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("Name", result[0].SqlName);
        Assert.Equal("Имя", result[0].DisplayName);
    }

    /// <summary>ColumnType.Number → дескриптор NumberColumnType (операторы =, &lt;&gt;, &gt;, &lt;).</summary>
    [Fact]
    public void Build_NumberColumn_HasNumberType()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Count", DisplayName = "Количество", ColumnType = ColumnType.Number },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("NumberColumnType", result[0].Type.GetType().Name);
    }

    /// <summary>ColumnType.Decimal → дескриптор DecimalColumnType.</summary>
    [Fact]
    public void Build_DecimalColumn_HasDecimalType()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Price", DisplayName = "Цена", ColumnType = ColumnType.Decimal },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("DecimalColumnType", result[0].Type.GetType().Name);
    }

    /// <summary>ColumnType.Date → дескриптор DateColumnType.</summary>
    [Fact]
    public void Build_DateColumn_HasDateType()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Created", DisplayName = "Создано", ColumnType = ColumnType.Date },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("DateColumnType", result[0].Type.GetType().Name);
    }

    /// <summary>ColumnType.Boolean → дескриптор BooleanColumnType.</summary>
    [Fact]
    public void Build_BooleanColumn_HasBooleanType()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Active", DisplayName = "Активен", ColumnType = ColumnType.Boolean },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("BooleanColumnType", result[0].Type.GetType().Name);
    }

    /// <summary>Исключённая колонка не попадает в результат (регистронезависимо).</summary>
    [Fact]
    public void Build_ExcludedColumn_IsRemoved()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Name", DisplayName = "Имя" },
            new() { SqlName = "Code", DisplayName = "Код" },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, ["CODE"]);

        Assert.Single(result);
        Assert.Equal("Name", result[0].SqlName);
    }

    /// <summary>Дубликаты SqlName — остаётся первое вхождение.</summary>
    [Fact]
    public void Build_DuplicateSqlName_KeepsFirst()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Name", DisplayName = "Имя" },
            new() { SqlName = "name", DisplayName = "Дубль" },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("Имя", result[0].DisplayName); // первое вхождение
    }

    /// <summary>Options пробрасываются в ClayFilterColumnInfo.</summary>
    [Fact]
    public void Build_Options_PassedThrough()
    {
        var options = new ClayFilterOption[]
        {
            new() { Value = "1", Label = "Один" },
            new() { Value = "2", Label = "Два" },
        };

        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "Cat", DisplayName = "Категория", Options = options },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.NotNull(result[0].Options);
        Assert.Equal(2, result[0].Options!.Count);
    }

    /// <summary>BoolTrueLabel/BoolFalseLabel пробрасываются.</summary>
    [Fact]
    public void Build_BoolLabels_PassedThrough()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new()
            {
                SqlName = "Flag", DisplayName = "Флаг",
                ColumnType = ColumnType.Boolean,
                BoolTrueLabel = "Да",
                BoolFalseLabel = "Нет",
            },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("Да", result[0].BoolTrueLabel);
        Assert.Equal("Нет", result[0].BoolFalseLabel);
    }

    /// <summary>Пустой SqlName пропускается.</summary>
    [Fact]
    public void Build_BlankSqlName_Skipped()
    {
        var columns = new ClayTreeFilterColumn[]
        {
            new() { SqlName = "", DisplayName = "Пусто" },
            new() { SqlName = "Name", DisplayName = "Имя" },
        };

        var result = ClayTreeFilterColumnBuilder.Build(columns, []);

        Assert.Single(result);
        Assert.Equal("Name", result[0].SqlName);
    }
}
