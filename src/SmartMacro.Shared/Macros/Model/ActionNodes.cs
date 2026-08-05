using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

/// <summary>Нажимает <see cref="Key"/> в целевом окне (или окнах).</summary>
public sealed record KeyPressNode : MacroNode
{
    /// <summary>Виртуальная клавиша, которую надо нажать.</summary>
    public required VirtualKey Key { get; init; }

    /// <summary>Селектор разветвления; <c>null</c> = контекстное окно прогона.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}

/// <summary>
/// Клик левой кнопкой по точке в координатах клиентской области целевого окна (или окон).
/// Задано должно быть РОВНО одно из <see cref="Point"/> (литерал) /
/// <see cref="PointVar"/> (переменная прогона с точкой, например <c>"cursor"</c>): графы,
/// нарушающие это, помечает валидатор, а если такой всё же проскочит — исполнитель прерывает
/// прогон.
/// </summary>
public sealed record ClickNode : MacroNode
{
    /// <summary>Литеральная точка клика. Взаимоисключима с <see cref="PointVar"/>.</summary>
    public ScreenPoint? Point { get; init; }

    /// <summary>Имя переменной прогона, в которой лежит точка клика. Взаимоисключимо с <see cref="Point"/>.</summary>
    public string? PointVar { get; init; }

    /// <summary>Двойной клик вместо одинарного.</summary>
    public bool DoubleClick { get; init; }

    /// <summary>Селектор разветвления; <c>null</c> = контекстное окно прогона.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}

/// <summary>Приостанавливает сценарий на <see cref="Ms"/> миллисекунд. Цели нет — пауза общая для всего прогона.</summary>
public sealed record DelayNode : MacroNode
{
    /// <summary>Задержка в миллисекундах. Ноль или отрицательное значение = ничего не делать.</summary>
    public required int Ms { get; init; }

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}

/// <summary>Добавляет тег <see cref="Tag"/> целевому окну (или окнам) через реестр окон.</summary>
public sealed record AddTagNode : MacroNode
{
    /// <summary>Добавляемый тег. Поддерживает подстановку <c>{var}</c> из переменных прогона.</summary>
    public required string Tag { get; init; }

    /// <summary>Селектор разветвления; <c>null</c> = контекстное окно прогона.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}

/// <summary>Снимает тег <see cref="Tag"/> с целевого окна (или окон) через реестр окон.</summary>
public sealed record RemoveTagNode : MacroNode
{
    /// <summary>Снимаемый тег. Поддерживает подстановку <c>{var}</c> из переменных прогона.</summary>
    public required string Tag { get; init; }

    /// <summary>Селектор разветвления; <c>null</c> = контекстное окно прогона.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}

/// <summary>Ставит целевому окну (или окнам) иконку в заголовке и на панели задач из <see cref="IconPath"/>.</summary>
public sealed record SetIconNode : MacroNode
{
    /// <summary>Путь к файлу иконки, например <c>"icons/{tag}.png"</c>. Поддерживает подстановку <c>{var}</c>.</summary>
    public required string IconPath { get; init; }

    /// <summary>Селектор разветвления; <c>null</c> = контекстное окно прогона.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}

/// <summary>
/// Запускает СВОЙ под-макрос как подпрогон. С <see cref="Target"/> — по параллельному подпрогону
/// на каждое подошедшее окно (это окно и становится контекстом подпрогона). Без него — один
/// подпрогон на контекстном окне текущего прогона.
///
/// <b>Звать можно только своё (волна F4).</b> До неё эта нода несла ИМЯ макроса и звала любой
/// макрос библиотеки — из-за чего отданный получателю файл молча не работал. Теперь адресатом
/// служит <see cref="SubmacroId"/> — под-макрос из <c>submacro/</c> того же бандла (см.
/// <see cref="MacroSubmacro"/>), и потерять его при передаче файла невозможно.
///
/// Из «плоско» следуют две вещи, которых здесь больше нет: подстановки <c>{var}</c> в адресате
/// (guid не собирают из строки) и вложенности глубже одного уровня — под-макрос, содержащий эту
/// ноду, отвергает валидатор, а попавший в файл руками обрывает прогон.
///
/// Подпрогоны получают КОПИЮ переменных родителя: чтение наследуется, записи наружу не протекают
/// никогда. Валидатор предупреждает, когда родитель читает переменную, которую пишет только
/// вызванный под-макрос, — молчаливая потеря значения тут была бы неотличима от несработавшего
/// распознавания.
/// </summary>
public sealed record RunSubmacroNode : MacroNode
{
    /// <summary>
    /// Под-макрос этого же бандла. <see cref="Guid.Empty"/> = адресат не выбран (ошибка
    /// валидации, а не «ничего не делать»).
    /// </summary>
    public required Guid SubmacroId { get; init; }

    /// <summary>Селектор разветвления; <c>null</c> = контекстное окно прогона.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary><c>true</c> (по умолчанию) = дождаться подпрогонов, прежде чем идти по <see cref="Next"/>; <c>false</c> = запустить и забыть.</summary>
    public bool Await { get; init; } = true;

    /// <summary>Следующая нода; <c>null</c> = конец прогона.</summary>
    public Guid? Next { get; init; }
}
