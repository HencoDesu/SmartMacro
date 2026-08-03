// ReSharper disable once CheckNamespace — имена SmartMacro.Macros.* достались модели от жизни в Core.

namespace SmartMacro.Macros.Model;

/// <summary>
/// Макрос как ориентированный граф нод: действия (одно исходящее ребро) и условные ноды (по
/// ребру на исход). Исполнение начинается со <see cref="StartNodeId"/> и идёт по рёбрам, пока
/// <c>null</c>-ребро не завершит прогон. Название типа отделяет граф от макроса как понятия
/// для пользователя (файла в <c>macros/</c>); старая плоская модель, с которой когда-то
/// приходилось не сталкиваться именами, давно удалена.
/// </summary>
public sealed class MacroGraph
{
    /// <summary>
    /// Имя макроса. Уникально в пределах библиотеки; становится именем файла на диске, поэтому
    /// обязано быть допустимым именем NTFS (за этим следит хранилище, а не модель).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Способы, которыми макрос запускается сам. Пусто — тоже законно: такой макрос
    /// запускается только через <see cref="RunMacroNode"/> из другого макроса или вручную из
    /// интерфейса.
    /// </summary>
    public List<MacroTrigger> Triggers { get; init; } = [];

    /// <summary>
    /// Нода, с которой начинается исполнение. Обязана ссылаться на элемент <see cref="Nodes"/>;
    /// <see cref="Guid.Empty"/> = стартовая нода не задана, и это ошибка валидации, а не
    /// «начинать с первой».
    /// </summary>
    public required Guid StartNodeId { get; init; }

    /// <summary>Все ноды графа. Их id обязаны быть уникальны (проверяют и валидатор, и исполнитель).</summary>
    public required List<MacroNode> Nodes { get; init; }
}
