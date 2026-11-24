using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class SlBlTests
{
    [Theory]
    [InlineData("a+b%25@example.invalid:0", "a+b%25@example.invalid", SlBlKind.Safe)]
    [InlineData("a@example.invalid\u001f1", "a@example.invalid", SlBlKind.Blocked)]
    [InlineData("a@example.invalid", "a@example.invalid", null)]
    public void PreservesTheDecodedMailboxKey(string input, string address, SlBlKind? kind)
    {
        Assert.True(SlBlKey.TryParse(input, out var key));
        Assert.Equal(new SlBlKey(address, kind), key);
    }

    [Theory]
    [InlineData("a@example.invalid:2")]
    [InlineData("a@example.invalid:1:0")]
    [InlineData(":1")]
    public void RejectsUnknownListKind(string input) => Assert.False(SlBlKey.TryParse(input, out _));

    [Theory]
    [InlineData("", "", SlBlKind.Blocked)]
    [InlineData("a", "", SlBlKind.Safe)]
    [InlineData("", "b", SlBlKind.Blocked)]
    [InlineData("a", "b", null)]
    public void KeepsTheLegacyInitialSelection(string safe, string blocked, SlBlKind? kind) =>
        Assert.Equal(kind, new SlBlLists { SafeList = safe, BlockedList = blocked }.InitialKind);

    [Fact]
    public void AllowsConfirmedSeparatorsAndChecksLengthWithoutTruncating()
    {
        Assert.Null(SlBlData.Validate("a@example.invalid;server.invalid,\t\r\ndomain.invalid\u00a0next.invalid"));
        Assert.Null(SlBlData.Validate(""));
        Assert.Null(SlBlData.Validate(new string('a', 7950)));
        Assert.NotNull(SlBlData.Validate(new string('a', 7951)));
        Assert.NotNull(SlBlData.Validate("<invalid>"));
    }

    [Fact]
    public void DraftRootSelectorIsExplicitAndUniquePerForm()
    {
        var first = new Kesco.App.Web.Inventory.Components.Pages.Equipment.SlBlForm();
        var second = new Kesco.App.Web.Inventory.Components.Pages.Equipment.SlBlForm();
        static string Selector(Kesco.App.Web.Inventory.Components.Pages.Equipment.SlBlForm form) =>
            (string)form.GetType().GetProperty("DraftRootSelector", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(form)!;
        static string RootClass(Kesco.App.Web.Inventory.Components.Pages.Equipment.SlBlForm form) =>
            ((Clayzor.Lib.Web.Controls.Components.Form.ClayEntityFormOptions)form.GetType()
                .GetField("_formOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(form)!).Class!;

        var firstSelector = Selector(first);
        var secondSelector = Selector(second);
        Assert.StartsWith(".slbl-draft-", firstSelector);
        Assert.NotEqual(firstSelector, secondSelector);
        Assert.Equal(firstSelector[1..], RootClass(first));
        Assert.Equal(secondSelector[1..], RootClass(second));
    }

    [Fact]
    public async Task EmptyReadAndSqlErrorRemainDifferent()
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(), Script.Error(new InvalidOperationException("lost"))]));
        using var db = new DbManager("Server=test", null, () => connection);
        Assert.Null(await SlBlData.ReadAsync(db, "a@example.invalid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SlBlData.ReadAsync(db, "a@example.invalid"));
    }
}
