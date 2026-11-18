using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки чистой матрицы доступности формы телефона.</summary>
public sealed class PhoneFormRulesTests
{
    /// <summary>Розетка поддерживается только цифровым и аналоговым типами.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(10, false)]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(200, false)]
    public void Socket_FollowsLegacyTypeMatrix(int type, bool expected)
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneTypeId = type });
        Assert.Equal(expected, result.Socket.IsEditable);
    }

    /// <summary>Оборудование поддерживает DECT и все типы от IP, но не короткие номера.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(10, true)]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(200, true)]
    [InlineData(201, true)]
    public void Equipment_FollowsLegacyTypeMatrix(int type, bool expected)
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneTypeId = type });
        Assert.Equal(expected, result.Equipment.IsEditable);
    }

    /// <summary>Группа перехвата допускает CCM и станции 1–99, а не остальные.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void PickupGroup_UsesStationRange(int? station, bool expected)
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneStationId = station });
        Assert.Equal(expected, result.PickupGroup.IsEditable);
    }

    /// <summary>Значение группы 0 сохраняется как несовместимое, а не теряется при смене станции.</summary>
    [Fact]
    public void UnsupportedFields_KeepExistingValuesReadOnly()
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput
        {
            PhoneTypeId = 100,
            PhoneStationId = 100,
            HasSocket = true,
            HasPort = true,
            HasPickupGroup = true,
            HasAutoDial = true,
            HasDrivenBy = true,
            HasDisplayedFrom = true,
            HasParallelCalls = true,
            HasGroupMembers = true,
            HasEquipment = true
        });

        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.Socket.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.PickupGroup.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.AutoDial.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.DrivenBy.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.DisplayedFrom.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.ParallelCalls.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.GroupMembers.State);
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, result.Equipment.State);
        Assert.True(result.Equipment.IsVisible);
        Assert.False(result.Equipment.IsEditable);
    }

    /// <summary>Неподдерживаемые пустые поля скрываются.</summary>
    [Fact]
    public void UnsupportedEmptyFields_AreHidden()
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneTypeId = 100, PhoneStationId = 100 });
        Assert.Equal(PhoneFieldState.Hidden, result.Socket.State);
        Assert.Equal(PhoneFieldState.Hidden, result.Equipment.State);
        Assert.Equal(PhoneFieldState.Hidden, result.ParallelCalls.State);
    }

    /// <summary>Возможности станции применяются только для типов зависимых номеров.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void DrivenAndDisplayed_RequireSupportedTypeAndStation(int type, bool expected)
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput
        {
            PhoneTypeId = type,
            PhoneStationId = 32,
            StationSupportsDrivenBy = true,
            StationSupportsDisplayedFrom = true
        });
        Assert.Equal(expected, result.DrivenBy.IsEditable);
        Assert.Equal(expected, result.DisplayedFrom.IsEditable);
    }

    /// <summary>Порт, автонабор и параллельные вызовы требуют выбранного контекста.</summary>
    [Fact]
    public void StationCapabilities_RequireSelectedTypeAndStation()
    {
        var result = PhoneFormRules.Evaluate(new PhoneFormRuleInput
        {
            PhoneTypeId = 1,
            PhoneStationId = 32,
            StationHasPorts = true,
            AutoDialAvailable = true,
            StationSupportsParallelCalls = true
        });
        Assert.True(result.Port.IsEditable);
        Assert.True(result.AutoDial.IsEditable);
        Assert.True(result.ParallelCalls.IsEditable);

        var missingStation = PhoneFormRules.Evaluate(new PhoneFormRuleInput
        {
            PhoneTypeId = 1,
            StationHasPorts = true,
            AutoDialAvailable = true,
            StationSupportsParallelCalls = true
        });
        Assert.False(missingStation.Port.IsVisible);
        Assert.False(missingStation.AutoDial.IsVisible);
        Assert.False(missingStation.ParallelCalls.IsVisible);
    }

    /// <summary>PBX-группа требует станцию, а основной номер — сотрудника.</summary>
    [Fact]
    public void GroupAndDefaultPhone_RequireTheirBusinessContext()
    {
        var group = PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneTypeId = 0, PhoneStationId = 0 });
        Assert.True(group.GroupMembers.IsEditable);
        var noStation = PhoneFormRules.Evaluate(new PhoneFormRuleInput { PhoneTypeId = 0 });
        Assert.False(noStation.GroupMembers.IsVisible);

        var employee = PhoneFormRules.Evaluate(new PhoneFormRuleInput { HasEmployee = true, HasDefaultPhone = true });
        Assert.True(employee.DefaultPhone.IsEditable);
        var noEmployee = PhoneFormRules.Evaluate(new PhoneFormRuleInput { HasDefaultPhone = true });
        Assert.Equal(PhoneFieldState.IncompatibleExistingValue, noEmployee.DefaultPhone.State);
    }
}
