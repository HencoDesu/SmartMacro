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
    public ScreenPoint TopLeft => new(X, Y);
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}
