using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Поведение карточки при явном запросе создания территории.</summary>
public sealed class TerritorialEntityCreateRouteTests
{
    /// <summary>Недоступное создание показывает сообщение без пустого редактора и загрузки типов.</summary>
    [Theory]
    [InlineData(null, false, "Создание территории пока недоступно.", 0)]
    [InlineData("42", true, "Создание территории пока недоступно.", 1)]
    [InlineData("42", false, "Родительская территория не найдена или недоступна.", 1)]
    [InlineData("0", false, "Родительская территория не найдена или недоступна.", 0)]
    public async Task CreateRouteDoesNotLoadUnavailableEditor(string? parent, bool parentExists,
        string expectedMessage, int expectedReads)
    {
        var script = new Queue<ScriptEntry>();
        if (parent == "42")
        {
            script.Enqueue(parentExists ? Script.Rows(new Dictionary<string, object?>
            {
                ["Id"] = 42, ["Type"] = 2, ["Name"] = "Родитель",
            }) : Script.Rows());
        }
        // Набор типов позволял прежней версии создать пустую карточку.
        script.Enqueue(Script.Rows());
        using var connection = new ScriptedConnection(script);
        using var db = new DbManager("Server=test", null, () => connection);
        await using var card = new Card(db, parent);

        var (state, message) = await card.LoadAsync();

        Assert.Null(state);
        Assert.Equal(expectedMessage, message);
        Assert.Equal(expectedReads, connection.CommandLog.Count);
    }

    /// <summary>Карточка существующей территории остаётся доступна для просмотра.</summary>
    [Fact]
    public async Task ExistingCardLoadsRecordForReadOnlyView()
    {
        var script = new Queue<ScriptEntry>([Script.Rows(new Dictionary<string, object?>
        {
            ["Id"] = 42, ["Name"] = "Территория", ["CountryCode"] = "001",
        }), Script.Rows()]);
        using var connection = new ScriptedConnection(script);
        using var db = new DbManager("Server=test", null, () => connection);
        await using var card = new Card(db, null, "42");

        var (state, message) = await card.LoadAsync();

        Assert.NotNull(state);
        Assert.Equal(42, state.Record.Id);
        Assert.Equal("001", state.Record.CountryCode);
        Assert.Equal("Карточка открыта для просмотра.", message);
        Assert.Equal(2, connection.CommandLog.Count);
    }

    private sealed class Card : TerritorialEntityForm
    {
        internal Card(DbManager db, string? parent, string id = "0")
        {
            Db = db;
            QueryId = id;
            QueryParent = parent;
        }

        internal async Task<(FormState? State, string? Message)> LoadAsync() =>
            (await LoadCardAsync(CancellationToken.None), Message);
    }
}
