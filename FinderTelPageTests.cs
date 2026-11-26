using System.Data;
using System.Data.Common;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Pages.Default;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Маршруты чтения, выбора и отмены в FinderTel.</summary>
public sealed class FinderTelPageTests
{
    private const string RequestId = "f0de1f3077ad485496d048ae0dc92442";
    private const string ReturnValue = "claySelectAdvancedSearch.returnValue";

    /// <summary>Из одного или нескольких кандидатов возвращается ровно один числовой ключ.</summary>
    [Theory]
    [InlineData("/Default/FinderTel", 1)]
    [InlineData("/Default/FinderTel.aspx", 2)]
    public async Task SingleReturnPreservesDisplayedNumberAndReturnsOnlyChosenId(string path, int count)
    {
        using var database = new FinderConnection(count);
        await using var context = Configure(database, path);
        context.JSInterop.Setup<bool>(ReturnValue, _ => true).SetResult(true);
        var page = context.Render<FinderTel>();
        page.WaitForAssertion(() => Assert.Equal(count, ChoiceButtons(page).Length));
        Assert.Contains("+7 (001) 002-03", page.Markup);
        Assert.Contains("0012", page.Markup);

        ChoiceButtons(page)[count - 1].Click();

        var invocation = Assert.Single(context.JSInterop.Invocations[ReturnValue]);
        Assert.Equal(RequestId, invocation.Arguments[0]);
        Assert.Equal(16 + count, Assert.IsType<int>(invocation.Arguments[1]));
        Assert.Equal(1, database.CandidateReads);
        Assert.Equal(16 + count, database.ResolvedPhoneId);
        Assert.All(ChoiceButtons(page), button => Assert.True(button.HasAttribute("disabled")));
    }

    /// <summary>Пустая выдача не отправляет результат, а отмена только закрывает окно.</summary>
    [Fact]
    public async Task EmptyPageCancelsWithoutReturningAValue()
    {
        using var database = new FinderConnection(0);
        await using var context = Configure(database, "/Default/FinderTel");
        var page = context.Render<FinderTel>();
        page.WaitForAssertion(() => Assert.Equal(1, database.PageReads));
        Assert.Empty(ChoiceButtons(page));

        page.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").Click();

        Assert.Single(context.JSInterop.Invocations["claySelectAdvancedSearch.close"]);
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == ReturnValue);
        Assert.Equal(0, database.CandidateReads);
    }

    /// <summary>Запрос множественного возврата оставляет список доступным только для просмотра.</summary>
    [Fact]
    public async Task MultipleReturnModeCannotSubmitASinglePhone()
    {
        using var database = new FinderConnection(2);
        await using var context = Configure(database, "/Default/FinderTel", mode: 2);
        var page = context.Render<FinderTel>();

        page.WaitForAssertion(() => Assert.Equal(2, ChoiceButtons(page).Length));
        Assert.All(ChoiceButtons(page), button => Assert.True(button.HasAttribute("disabled")));
        Assert.Contains("Возврат по этой ссылке недоступен.", page.Markup);
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == ReturnValue);
    }

    /// <summary>Номер, исчезнувший после загрузки списка, не возвращается вызывающей форме.</summary>
    [Fact]
    public async Task StaleCandidateDoesNotReturnAValue()
    {
        using var database = new FinderConnection(1, candidateAvailable: false);
        await using var context = Configure(database, "/Default/FinderTel");
        var page = context.Render<FinderTel>();
        page.WaitForAssertion(() => Assert.Single(ChoiceButtons(page)));

        ChoiceButtons(page).Single().Click();

        Assert.Contains("Выбранная запись стала недоступна.", page.Markup);
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == ReturnValue);
        Assert.Equal(17, database.ResolvedPhoneId);
    }

    private static AngleSharp.Dom.IElement[] ChoiceButtons(IRenderedComponent<FinderTel> page) =>
        page.FindAll("button").Where(button => button.TextContent.Trim() == "Выбрать").ToArray();

    private static BunitContext Configure(FinderConnection database, string path, int mode = 1)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped(_ => new DbManager("Server=test", null, () => database));
        context.Services.AddSingleton<AuthenticationStateProvider, TestAuthenticationStateProvider>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddScoped<ClayErrorService>();
        context.Services.AddMudServices();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"{path}?return={mode}&requestId={RequestId}&returnOrigin=http%3A%2F%2Flocalhost");
        return context;
    }

    private sealed class FinderConnection(int count, bool candidateAvailable = true) : DbConnection
    {
        private ConnectionState _state;
        internal int PageReads { get; private set; }
        internal int CandidateReads { get; private set; }
        internal int? ResolvedPhoneId { get; private set; }

        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command(this);

        private DbDataReader Read(DbCommand command)
        {
            var sql = command.CommandText;
            if (sql.Contains("SUSER_SID()", StringComparison.Ordinal))
                return Table("КодСотрудника", 4).CreateDataReader();
            if (sql.Contains("dbo.ТипыТелефонныхНомеров", StringComparison.Ordinal))
                return new DataTable().CreateDataReader();
            if (sql.Contains("SELECT COUNT(*)", StringComparison.Ordinal))
            {
                PageReads++;
                var rows = new DataTable();
                rows.Columns.Add("Id", typeof(int));
                rows.Columns.Add("FullNumber", typeof(string));
                rows.Columns.Add("ShortNumber", typeof(string));
                for (var index = 0; index < count; index++)
                    rows.Rows.Add(17 + index, $"+7 (001) 002-0{3 + index}", $"001{2 + index}");
                return new DataTableReader([Table("Total", count), rows]);
            }
            if (sql.Contains("@SelectedPhoneId", StringComparison.Ordinal))
            {
                CandidateReads++;
                ResolvedPhoneId = (int)command.Parameters["SelectedPhoneId"].Value!;
                var row = Table("Id", ResolvedPhoneId.Value);
                return (candidateAvailable ? row : row.Clone()).CreateDataReader();
            }
            throw new InvalidOperationException(sql);
        }

        private static DataTable Table(string column, int value)
        {
            var table = new DataTable();
            table.Columns.Add(column, typeof(int));
            table.Rows.Add(value);
            return table;
        }

        private sealed class Command(FinderConnection owner) : DbCommand
        {
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            public override void Cancel() { }
            public override void Prepare() { }
            public override int ExecuteNonQuery() => throw new NotSupportedException();
            public override object ExecuteScalar() => throw new NotSupportedException();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => owner.Read(this);
        }
    }
}
