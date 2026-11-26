using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Pages.Default;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Маршрут и возврат выбранного оборудования из Finder.</summary>
public sealed class FinderRouteTests
{
    private const string FirstRequest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondRequest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ReturnValue = "claySelectAdvancedSearch.returnValue";

    /// <summary>Поздний ответ interop не блокирует выбор в другом контексте.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldRouteInteropReplyCannotChangeCurrentPicker(bool posted)
    {
        using var db = new FinderConnection();
        await using var context = CreateContext(db);
        var firstReply = context.JSInterop.Setup<bool>(ReturnValue, call => Equals(call.Arguments[0], FirstRequest));
        var secondReply = context.JSInterop.Setup<bool>(ReturnValue, call => Equals(call.Arguments[0], SecondRequest)).SetResult(true);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(Route(FirstRequest, station: 4));
        var page = context.Render<Finder>();
        var oldSelection = Pick(page).ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.Single(firstReply.Invocations));

        await page.InvokeAsync(() => navigation.NavigateTo(Route(SecondRequest, station: 5)));
        page.WaitForAssertion(() => Assert.Contains("Станция 5", page.Markup));

        firstReply.SetResult(posted);
        await oldSelection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Pick(page).GetAttribute("disabled"));
        Assert.DoesNotContain("Вызывающее окно недоступно", page.Markup);
        await Pick(page).ClickAsync(new MouseEventArgs());

        var call = Assert.Single(secondReply.Invocations);
        Assert.Equal(new PhoneEquipmentKey(17, 39), call.Arguments[1]);
        Assert.Equal([4, 5], db.AvailabilityStations);
    }

    /// <summary>Сценарии просмотра и отмены не возвращают ключ или пустое принятое значение.</summary>
    [Theory]
    [InlineData("1", FirstRequest, "http://localhost", false, false)]
    [InlineData("2", FirstRequest, "http://localhost", true, false)]
    [InlineData("1", "invalid", "http://localhost", true, false)]
    [InlineData("1", FirstRequest, "https://other.invalid", true, false)]
    [InlineData("1", FirstRequest, "http://localhost", false, true)]
    public async Task CancelNeverSubmitsSelection(string mode, string request, string origin, bool disabled, bool empty)
    {
        using var db = new FinderConnection { Empty = empty };
        await using var context = CreateContext(db);
        var close = context.JSInterop.SetupVoid("claySelectAdvancedSearch.close").SetVoidResult();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(Route(request, station: 4, mode, origin));
        var page = context.Render<Finder>();
        page.WaitForAssertion(() => Assert.Equal(2, db.ReadCount));

        if (empty)
            Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Выбрать");
        else
            Assert.Equal(disabled, Pick(page).HasAttribute("disabled"));
        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync(new MouseEventArgs());

        Assert.Single(close.Invocations);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == ReturnValue);
        Assert.Empty(db.AvailabilityStations);
    }

    /// <summary>Перед отправкой ключ повторно проверяется по контексту выбора.</summary>
    [Fact]
    public async Task CandidateThatBecameUnavailableIsNeverReturned()
    {
        using var db = new FinderConnection { Unavailable = true };
        await using var context = CreateContext(db);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(Route(FirstRequest, station: 4));
        var page = context.Render<Finder>();
        await Pick(page).ClickAsync(new MouseEventArgs());

        Assert.Contains("Выбранная запись стала недоступна", page.Markup);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == ReturnValue);
        Assert.Equal([4], db.AvailabilityStations);
    }

    private static string Route(string request, int station, string mode = "1", string origin = "http://localhost") =>
        $"/Default/Finder.aspx?return={mode}&requestId={request}&returnOrigin={Uri.EscapeDataString(origin)}&station={station}";

    private static AngleSharp.Dom.IElement Pick(IRenderedComponent<Finder> page)
    {
        page.WaitForAssertion(() => Assert.Contains(page.FindAll("button"), button => button.TextContent.Trim() == "Выбрать"));
        return page.FindAll("button").Single(button => button.TextContent.Trim() == "Выбрать");
    }

    private static BunitContext CreateContext(FinderConnection connection)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<ClayErrorService>();
        context.Services.AddSingleton<ClayFormWindowService>();
        context.Services.AddSingleton(new DbManager("Server=test", null, () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, FinderAuthentication>();
        return context;
    }

    private sealed class FinderAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, @"TEST\finder")], "Negotiate"))));
    }

    // ReadPageAsync использует два набора результатов; общий ScriptedDataReader обслуживает один.
    private sealed class FinderConnection : DbConnection
    {
        private ConnectionState _state;
        internal int ReadCount { get; private set; }
        internal List<int> AvailabilityStations { get; } = [];
        internal bool Empty { get; init; }
        internal bool Unavailable { get; init; }

        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FinderCommand(this);

        private DbDataReader Read(string sql, DbParameterCollection parameters)
        {
            ReadCount++;
            if (sql.Contains("@WindowsPrincipal", StringComparison.Ordinal))
            {
                var employee = new DataTable();
                employee.Columns.Add("Id", typeof(int));
                employee.Rows.Add(1);
                return employee.CreateDataReader();
            }

            var equipment = new DataTable();
            equipment.Columns.Add("EquipmentId", typeof(int));
            equipment.Columns.Add("ConnectionEquipmentId", typeof(int));
            equipment.Columns.Add("Model", typeof(string));
            int station = (int)parameters["StationId"].Value!;
            if (parameters.Contains("SelectedEquipmentId"))
            {
                AvailabilityStations.Add(station);
                Assert.Equal(17, parameters["SelectedEquipmentId"].Value);
                Assert.Equal(39, parameters["SelectedConnectionId"].Value);
                if (!Unavailable)
                    equipment.Rows.Add(17, 39, $"Станция {station}");
                return equipment.CreateDataReader();
            }

            var total = new DataTable();
            total.Columns.Add("Total", typeof(int));
            total.Rows.Add(Empty ? 0 : 1);
            if (!Empty)
                equipment.Rows.Add(17, 39, $"Станция {station}");
            return new DataTableReader([total, equipment]);
        }

        private sealed class FinderCommand(FinderConnection owner) : DbCommand
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
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => owner.Read(CommandText, DbParameterCollection);
            public override int ExecuteNonQuery() => throw new NotSupportedException();
            public override object? ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() { }
            public override void Cancel() { }
        }
    }
}
