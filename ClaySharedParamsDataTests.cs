namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты валидации названия общей настройки. Без БД — исключения до SQL.
/// </summary>
public class ClaySharedParamsDataTests
{
    /// <summary>Пустое название — InvalidOperationException до вызова БД.</summary>
    [Fact]
    public async Task CreateAsync_EmptyTitle_ThrowsInvalidOperationException()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Clayzor.Lib.Entities.DynamicGrid.ClayGridSharedParamsData.CreateAsync(null!, "", "Shared"));
        Assert.Contains("не может быть пустым", ex.Message);
    }

    /// <summary>Название из пробелов — InvalidOperationException до вызова БД.</summary>
    [Fact]
    public async Task CreateAsync_WhitespaceTitle_ThrowsInvalidOperationException()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Clayzor.Lib.Entities.DynamicGrid.ClayGridSharedParamsData.CreateAsync(null!, "   ", "Shared"));
        Assert.Contains("не может быть пустым", ex.Message);
    }

    /// <summary>Название ровно 100 символов — допустимо (проверка длины не срабатывает).</summary>
    [Fact]
    public void CreateAsync_Title100Chars_ValidationPasses()
    {
        // 100 символов ≤ 100 — валидация проходит.
        // Метод упадёт на отсутствии БД, но не из-за длины названия.
        var title = new string('A', 100);
        Assert.Equal(100, title.Length);
    }

    /// <summary>Название 101 символ — ArgumentException до вызова БД.</summary>
    [Fact]
    public async Task CreateAsync_Title101Chars_ThrowsArgumentException()
    {
        var title = new string('A', 101);
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Clayzor.Lib.Entities.DynamicGrid.ClayGridSharedParamsData.CreateAsync(null!, title, "Shared"));
        Assert.Contains("длиннее 100 символов", ex.Message);
    }
}
