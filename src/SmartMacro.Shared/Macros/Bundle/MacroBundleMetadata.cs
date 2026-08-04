using System.Text.Json;
using SmartMacro.Macros.Model;

namespace SmartMacro.Macros.Bundle;

/// <summary>
/// Содержимое <c>metadata.json</c> — паспорт бандла, читаемый ОТДЕЛЬНО от графа.
///
/// <b>Идентичность макроса — имя файла, а не <see cref="Id"/>.</b> Это сознательное исключение
/// из правила «идентичность — <c>Guid</c>, имя — подпись», принятого для нод (<see cref="MacroNode"/>):
/// для макроса как файла на диске имя прозрачнее для пользователя, а переименование делается
/// проводником и не должно требовать ничего от программы. <see cref="Id"/> при этом есть,
/// присваивается <see cref="CreateNew"/> и НЕ МЕНЯЕТСЯ НИКОГДА — дублирование макроса это
/// частный случай создания, то есть новый <see cref="Id"/>, а не копия старого. Сегодня поле
/// информационное, на будущее (ссылки на под-макросы в F4 пойдут по guid, потому что имя
/// переименовывается).
///
/// Тип — <c>record</c>, значит <c>with { Id = … }</c> синтаксически возможно. Так делать нельзя;
/// правило держится договорённостью, а не компилятором, и поэтому написано здесь.
/// </summary>
public sealed record MacroBundleMetadata
{
    /// <summary>
    /// Версия формата, которой сделан файл. См. <see cref="MacroBundleFormat.CurrentVersion"/>
    /// — поле существует ради того, чтобы нечитаемый файл давал «сделано другой версией», а не
    /// «повреждён».
    /// </summary>
    public required int FormatVersion { get; init; }

    /// <summary>Личность бандла. Выдаётся один раз при создании; см. примечание к типу.</summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Имя макроса, каким его задал автор. Библиотека показывает основу имени ФАЙЛА (она и есть
    /// идентичность), а это поле делает бандл самоописывающимся: файл, переименованный при
    /// пересылке, всё ещё помнит, как его звали у автора.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Описание своими словами; <c>null</c> — не заполнено (в файл тогда не пишется).</summary>
    public string? Description { get; init; }

    /// <summary>Автор; <c>null</c> — не заполнено. Ничем не проверяется, это подпись.</summary>
    public string? Author { get; init; }

    /// <summary>Когда бандл создан.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>Когда бандл последний раз записан.</summary>
    public required DateTimeOffset Modified { get; init; }

    /// <summary>
    /// Паспорт нового бандла: свежий <see cref="Id"/>, текущая версия формата, обе даты равны
    /// <paramref name="now"/>.
    /// </summary>
    /// <param name="name">Имя макроса.</param>
    /// <param name="description">Описание или <c>null</c>.</param>
    /// <param name="author">Автор или <c>null</c>.</param>
    /// <param name="now">Момент создания; <c>null</c> — <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static MacroBundleMetadata CreateNew(
        string name,
        string? description = null,
        string? author = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stamp = now ?? DateTimeOffset.UtcNow;
        return new MacroBundleMetadata
        {
            FormatVersion = MacroBundleFormat.CurrentVersion,
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Author = author,
            Created = stamp,
            Modified = stamp,
        };
    }

    /// <summary>
    /// Копия с обновлённым <see cref="Modified"/> — то, что делают при сохранении. Отдельный
    /// метод вместо <c>with</c> на месте вызова, чтобы «обновить дату» не соседствовало в одном
    /// выражении с полями, которые менять нельзя.
    /// </summary>
    public MacroBundleMetadata Touch(DateTimeOffset? now = null) =>
        this with { Modified = now ?? DateTimeOffset.UtcNow };
}

/// <summary>
/// (Де)сериализация <c>metadata.json</c>.
///
/// Диалект тот же, что у графа (<see cref="MacroGraphJson.Options"/>), и это не лень: обе записи
/// лежат в одном архиве и открываются одним и тем же человеком в одном и том же просмотрщике.
/// Важнее всего здесь <c>UnsafeRelaxedJsonEscaping</c> — имя и описание пишет автор, и «Загрузка
/// лучника» обязана читаться в файле как «Загрузка лучника», а не как россыпь <c>\u04??</c>.
/// </summary>
public static class MacroBundleMetadataJson
{
    /// <summary>Сериализует паспорт с отступами.</summary>
    public static string Serialize(MacroBundleMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonSerializer.Serialize(metadata, MacroGraphJson.Options);
    }

    /// <summary>
    /// Разбирает паспорт. Отсутствие обязательного поля — тоже <see cref="JsonException"/>:
    /// свойства помечены <c>required</c>, и <c>System.Text.Json</c> это соблюдает.
    /// </summary>
    /// <exception cref="JsonException">Документ не разбирается в паспорт.</exception>
    public static MacroBundleMetadata Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        MacroBundleMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<MacroBundleMetadata>(json, MacroGraphJson.Options);
        }
        catch (NotSupportedException ex)
        {
            // Та же нормализация, что и в MacroGraphJson: вызывающему хватает одного типа
            // исключения на все сбои разбора.
            throw new JsonException("Bundle metadata JSON is not deserializable.", ex);
        }

        return metadata ?? throw new JsonException("Bundle metadata JSON is 'null'.");
    }
}
