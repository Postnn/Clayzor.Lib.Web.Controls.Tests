# Clayzor.Lib.Web.Controls.Tests

Юнит-тесты для библиотеки компонентов [`Clayzor.Lib.Web.Controls`](https://github.com/Postnn/Clayzor.Lib.Web.Controls). Проверяют логику Blazor-грида `ClayGrid` и его окружения: построение SQL составного фильтра, серверную группировку, дескрипторы типов колонок, сериализацию состояния и фильтра, разбор URL-фильтра и биндинг опций.

> Тестовый проект решения (`IsTestProject=true`, `IsPackable=false`). NuGet-пакета не имеет; запускается через `dotnet test`.

## Содержание

- [Что покрывается](#что-покрывается)
- [Технологии и зависимости](#технологии-и-зависимости)
- [Состав тестов](#состав-тестов)
- [Запуск](#запуск)
- [Структура в решении](#структура-в-решении)
- [Как добавить тест](#как-добавить-тест)
- [Лицензия](#лицензия)

## Что покрывается

Тесты нацелены на **чистую, детерминированную логику** слоя Controls — построители SQL, мапперы, сериализаторы и парсеры, которые можно проверить без БД и без рендеринга Blazor-компонентов. Обращений к SQL Server в тестах нет: проверяются формируемый текст запроса, дерево фильтра, структура группировки и результаты сериализации.

## Технологии и зависимости

- **.NET 10** (`net10.0`), `Microsoft.NET.Sdk`, включены `ImplicitUsings` и `Nullable`.
- **xUnit** `2.9.*` — фреймворк тестирования (глобальный `using Xunit`).
- **xunit.runner.visualstudio** `3.0.*` — запуск из Visual Studio / `dotnet test`.
- **Microsoft.NET.Test.Sdk** `17.13.*` — инфраструктура тестового хоста.
- **Dapper** `2.*` — для типов/утилит, используемых в проверках построения SQL.
- **ProjectReference:** `Clayzor.Lib.Web.Controls` (тестируемая библиотека).

## Состав тестов

| Файл | Что проверяет |
| --- | --- |
| `ClayCompositeSqlBuilderTests.cs` | Построение фрагмента `WHERE` из дерева составного фильтра (`ClayCompositeSqlBuilder`): корректность операторов, параметризация, белый список колонок. |
| `ClayFilterDescriptionBuilderTests.cs` | Формирование текстового описания и кликабельных сегментов фильтра (`ClayFilterDescriptionBuilder`). |
| `ClayFilterJsonConverterTests.cs` | Полиморфная JSON-сериализация дерева фильтра с дискриминатором `$type` (`ClayFilterJsonConverter`). |
| `ClayFilterUrlHelperTests.cs` | Кодирование/декодирование фильтра в URL (дерево → JSON → Deflate → Base64Url и обратно). |
| `ClayGroupingEngineTests.cs` | Движок серверной группировки (`ClayGroupingEngine`): дерево групп, подсчёт `ItemCount`, многоуровневость. |
| `ColumnTypeMapTests.cs` | Сопоставление кодов типов колонок динамического грида существующим дескрипторам (`ClayColumnTypeMap`). |
| `ColumnTypeRegistryTests.cs` | Реестр дескрипторов типов колонок (`ColumnTypeRegistry`): резолвинг из CLR-типа и из enum. |
| `ComplexColumnTypesTests.cs` | Поведение составных типов колонок (список, иконка, HTML и т. п.). |
| `SpecialColumnTypesTests.cs` | Специальные типы колонок (например локальные дата/время из UTC, ограниченный текст). |
| `DefinitionMapperTests.cs` | Маппинг определений грида и колонок из БД в модели (`ClayGridDefinitionData` / мапперы). |
| `FilterModelTests.cs` | Модель фильтра: узлы дерева, глубокое копирование (`Clone`), инварианты `ColumnFilter`/`ValueFilter`. |
| `GridStateSerializationTests.cs` | Сериализация/десериализация состояния грида (колонки, сортировка, группировка, фильтр, размер страницы). |
| `UrlFilterParserTests.cs` | Разбор URL-фильтра вида `КлючURL=op~value` (`ClayGridUrlFilterParser`). |
| `OptionsBindingTests.cs` | Биндинг опций динамического грида (`ClayGridDynamicOptions`) из конфигурации и их валидация. |
| `UserParamsTests.cs` | Пользовательские параметры грида (`ClayGridUserParamsData`): построение имён и SQL сохранения/загрузки. |

## Запуск

Из каталога тестового проекта или решения:

```bash
dotnet test
```

Полезные варианты:

```bash
# Подробный вывод
dotnet test -v normal

# Отобрать тесты по имени
dotnet test --filter "FullyQualifiedName~ClayGroupingEngineTests"

# Собрать покрытие (при наличии соответствующего сборщика)
dotnet test --collect:"XPlat Code Coverage"
```

## Структура в решении

Ссылка на тестируемый проект в `.csproj` — `..\..\src\Clayzor.Lib.Web.Controls\...`, то есть решение использует раскладку с каталогами `src/` и `tests/`:

```
<solution>/
├─ src/
│  └─ Clayzor.Lib.Web.Controls/          тестируемая библиотека
└─ tests/
   └─ Clayzor.Lib.Web.Controls.Tests/    этот проект
```

## Как добавить тест

- Один тестовый класс на проверяемый тип/сценарий, имя файла — `{Тип}Tests.cs`, в namespace `Clayzor.Lib.Web.Controls.Tests`.
- Использовать xUnit: `[Fact]` для одиночных случаев, `[Theory]` + `[InlineData]`/`[MemberData]` для параметризованных (глобальный `using Xunit` уже подключён).
- Держать тесты детерминированными и без обращений к БД: проверять чистые функции (построители SQL, мапперы, сериализаторы, парсеры). Логика слоя Controls специально вынесена в такие функции — см. `AGENTS.md` тестируемого проекта.

## Лицензия

Проект распространяется под лицензией **Apache License 2.0** — полный текст в файле [`LICENSE`](LICENSE) в корне репозитория.

Copyright © 2026 Bulychev Nick
