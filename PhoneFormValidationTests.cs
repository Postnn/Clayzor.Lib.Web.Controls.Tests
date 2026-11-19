using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки чистой валидации и вычислений формы телефона.</summary>
public sealed class PhoneFormValidationTests
{
    /// <summary>Ведущие нули, # и * сохраняются как строковый номер.</summary>
    [Theory]
    [InlineData("0012")]
    [InlineData("#001")]
    [InlineData("*12#")]
    public void Number_PreservesLegacyStringSemantics(string number)
    {
        var issues = PhoneFormValidator.Validate(ValidRequest(number));
        Assert.DoesNotContain(issues, item => item.Field == "Number");
    }

    /// <summary>Обязательные поля и описание АТС блокируют сохранение.</summary>
    [Fact]
    public void RequiredFields_AreBlocking()
    {
        var request = new PhoneSaveRequest { Phone = new Phone(), Today = new DateTime(2026, 1, 1) };
        var issues = PhoneFormValidator.Validate(request);
        Assert.Contains(issues, item => item.Field == "Number" && item.Severity == PhoneValidationSeverity.Blocking);
        Assert.Contains(issues, item => item.Field == "PhoneTypeId");
        Assert.Contains(issues, item => item.Field == "StationId");
        Assert.Contains(issues, item => item.Field == "CustomerId");
        Assert.Contains(issues, item => item.Field == "EmployeeId");
        Assert.Contains(issues, item => item.Field == "AtsDescription");
    }

    /// <summary>Формат переадресации и отсутствие кириллицы проверяются независимо.</summary>
    [Theory]
    [InlineData("++123", "QuickCall")]
    [InlineData("12A", "AutoDialNumber")]
    [InlineData("Описание", "AtsDescription")]
    public void InvalidFormats_AreRejected(string value, string expectedField)
    {
        var request = ValidRequest();
        request.Phone.QuickCall = expectedField == "QuickCall" ? value : "+123";
        request.Phone.AutoDialNumber = expectedField == "AutoDialNumber" ? value : string.Empty;
        request.Phone.AtsDescription = expectedField == "AtsDescription" ? value : "Test";
        Assert.Contains(PhoneFormValidator.Validate(request), item => item.Field == expectedField);
    }

    /// <summary>Дата сегодня допустима, вчерашняя дата блокируется.</summary>
    [Fact]
    public void ReturnDate_UsesUtcBusinessDateBoundary()
    {
        var request = ValidRequest();
        request.Today = new DateTime(2026, 2, 10);
        request.Phone.ReturnDate = request.Today;
        Assert.DoesNotContain(PhoneFormValidator.Validate(request), item => item.Field == "ReturnDate");
        request.Phone.ReturnDate = request.Today.AddDays(-1);
        Assert.Contains(PhoneFormValidator.Validate(request), item => item.Field == "ReturnDate");
    }

    /// <summary>Параллельные вызовы запрещают дубликаты, самовызов и скрытие из справочника.</summary>
    [Fact]
    public void ParallelCalls_ValidateCollectionAndDirectory()
    {
        var request = ValidRequest();
        request.Phone.ViewInDirectory = 0;
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "101" });
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "101" });
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "102", DelayBeforeCall = -1 });
        var issues = PhoneFormValidator.Validate(request);
        Assert.Contains(issues, item => item.Field == "ParallelCalls" && item.Message.Contains("сам", StringComparison.OrdinalIgnoreCase) == false);
        Assert.Contains(issues, item => item.Field == "ViewInDirectory");
        Assert.Contains(issues, item => item.Message.Contains("повторно"));
        Assert.Contains(issues, item => item.Message.Contains("Задержки"));
    }

    /// <summary>Группа 0 допустима и отличается от пустого значения.</summary>
    [Fact]
    public void PickupGroup_ZeroIsValid()
    {
        var groups = PhonePickupGroups.ForStation(1, null);
        Assert.Contains("0", groups);
        Assert.Equal(16, groups.Count);
        var request = ValidRequest();
        request.Phone.PickupGroup = "0";
        Assert.DoesNotContain(PhoneFormValidator.Validate(request), item => item.Field == "PickupGroup");
    }

    /// <summary>Полный номер имеет приоритет отображаемого, затем ведущего и префикса.</summary>
    [Fact]
    public void FullNumber_FollowsLegacyPriority()
    {
        var station = new PhoneStationLookup { Prefix = "7495", PrefixUsed = "4%" };
        Assert.Equal("displayed", PhoneFormValidator.CalculateFullNumber("101", "displayed", "driven", station));
        Assert.Equal("driven", PhoneFormValidator.CalculateFullNumber("101", null, "driven", station));
        Assert.Equal("7495401", PhoneFormValidator.CalculateFullNumber("401", null, null, station));
        Assert.Equal("101", PhoneFormValidator.CalculateFullNumber("101", null, null, station));
    }

    /// <summary>LIKE-маски экранируются как данные, а не как пользовательский шаблон.</summary>
    [Fact]
    public void EscapeLike_PreventsWildcardInjection() => Assert.Equal("a[%]b[_]c[[]", PhoneFormValidator.EscapeLike("a%b_c["));

    /// <summary>Временные значения услуг восстанавливают выбор пользователя.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 4)]
    [InlineData(101, 4)]
    [InlineData(1, 3)]
    [InlineData(200, 3)]
    public void Services_RestoreUserChoice(int type, int expected)
    {
        Assert.Equal(expected, PhoneTypeChoiceRules.ResolveServices(type, 3));
        Assert.Equal(3, PhoneTypeChoiceRules.ResolveServices(1, 3));
    }

    private static PhoneSaveRequest ValidRequest(string number = "101") => new()
    {
        Phone = new Phone
        {
            Number = number, FullNumber = number, PhoneTypeId = 1, StationId = 1, CustomerId = 1,
            EmployeeId = 4, AtsDescription = "Test", Connected = "Room", ServicesKind = 1, ViewInDirectory = 1,
        },
        Today = new DateTime(2026, 1, 1),
        AvailablePickupGroups = ["0"],
    };
}
