namespace SmartMacro.Native;

/// <summary>
/// Точка в пиксельных координатах. Используется везде, где иначе пришлось бы передавать
/// пару (x, y): координаты клика, положение курсора, расположение кнопок. Живёт в
/// пространстве имён Native рядом с остальными примитивными типами (VirtualKey,
/// MouseButton).
/// </summary>
/// <remarks>
/// Сделано record struct, чтобы тип чисто проходил round trip через привязку
/// Microsoft.Extensions.Configuration (у позиционных параметров конструктора под капотом
/// есть init-сеттеры). Значения по умолчанию у параметров позволяют биндеру построить
/// объект через конструктор без аргументов, если секция JSON пуста.
/// </remarks>
public readonly record struct ScreenPoint(int X = 0, int Y = 0)
{
    public override string ToString() => $"({X},{Y})";
}

/// <summary>
/// Прямоугольник в пиксельных координатах. Используется для областей обрезки в машинном
/// зрении, для габаритов кнопок — для всего, чему нужны (x, y, w, h). Тот же приём с
/// record struct ради привязки конфигурации, что и у <see cref="ScreenPoint"/>.
/// </summary>
public readonly record struct ScreenRect(int X = 0, int Y = 0, int Width = 0, int Height = 0)
{
    // [JsonIgnore] на всех трёх — не вкусовщина, а починка формата файлов, которые правят руками.
    // Свойства вычисляемые и БЕЗ СЕТТЕРОВ, поэтому на чтении System.Text.Json их игнорирует и так;
    // а вот на записи он их прилежно выводил, и каждая область в settings.json и в macros/*.json
    // получала лишние "TopLeft", "Right" и "Bottom". Для человека, который открыл файл в блокноте,
    // это три поля, выглядящие настраиваемыми и молча ничего не делающие: поправишь Right —
    // ширина не изменится, и понять почему нельзя. Найдено глазами, в первом же созданном
    // settings.json.
    [System.Text.Json.Serialization.JsonIgnore]
    public ScreenPoint TopLeft => new(X, Y);

    [System.Text.Json.Serialization.JsonIgnore]
    public int Right => X + Width;

    [System.Text.Json.Serialization.JsonIgnore]
    public int Bottom => Y + Height;

    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}
