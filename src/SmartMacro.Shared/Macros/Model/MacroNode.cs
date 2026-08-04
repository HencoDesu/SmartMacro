using System.Text.Json.Serialization;

namespace SmartMacro.Macros.Model;

/// <summary>
/// Расположение ноды в канве редактора. Чисто презентационные данные — исполнитель их никогда
/// не читает, но пройти round trip через JSON они обязаны, иначе разложенные вручную графы
/// потеряют свою раскладку.
/// </summary>
public sealed record NodeEditorInfo(double X, double Y);

/// <summary>
/// Одна нода <see cref="MacroGraph"/>. Полиморфна в JSON через <c>$type</c>.
///
/// Два семейства:
///   * Ноды действий — одно исходящее ребро (<c>Next</c>; <c>null</c> = конец прогона).
///     Большинство несёт необязательный <see cref="TargetSelector"/> (<c>Target</c>): не
///     <c>null</c> — разослать действие параллельно во все подходящие окна; <c>null</c> —
///     действовать на контекстное окно прогона.
///   * Условные ноды — работают только с контекстным окном и несут по одному исходящему ребру
///     на исход (Found/NotFound, Found/Timeout, Matched/NotMatched); <c>null</c>-ребро исхода
///     завершает прогон.
///
/// <b>Связь по <see cref="Id"/>, показ по <see cref="DisplayName"/>.</b> Раньше это было одно
/// поле: строка, которую пользователь правил руками и по которой же на ноду ссылались рёбра.
/// Отсюда следовало ровно то, чего быть не должно, — переименование становилось операцией НАД
/// ГРАФОМ (перенацелить каждое входящее ребро и, если не повезло, стартовую ноду), а два
/// одинаковых имени были ошибкой валидации, хотя по смыслу это подпись. Разделение убирает и то
/// и другое: <see cref="Id"/> не показывается и не редактируется, а <see cref="DisplayName"/> ни
/// на что не влияет, кроме читаемости.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(KeyPressNode), typeDiscriminator: "keyPress")]
[JsonDerivedType(typeof(ClickNode), typeDiscriminator: "click")]
[JsonDerivedType(typeof(DelayNode), typeDiscriminator: "delay")]
[JsonDerivedType(typeof(AddTagNode), typeDiscriminator: "addTag")]
[JsonDerivedType(typeof(RemoveTagNode), typeDiscriminator: "removeTag")]
[JsonDerivedType(typeof(SetIconNode), typeDiscriminator: "setIcon")]
[JsonDerivedType(typeof(RunMacroNode), typeDiscriminator: "runMacro")]
[JsonDerivedType(typeof(FindElementNode), typeDiscriminator: "findElement")]
[JsonDerivedType(typeof(WaitForElementNode), typeDiscriminator: "waitForElement")]
[JsonDerivedType(typeof(RecognizeTagNode), typeDiscriminator: "recognizeTag")]
public abstract record MacroNode
{
    /// <summary>
    /// Личность ноды. Рёбра, стартовая нода, точки останова и события прогона ссылаются на
    /// ноду именно по нему, и больше он не нужен НИГДЕ: в интерфейсе он не показывается и не
    /// редактируется (разве что подсказкой при отладке).
    ///
    /// Значение по умолчанию — свежий guid, чтобы «создать ноду» не требовало отдельного шага
    /// «выдумать ей ключ». Файл, в котором поле не выставлено руками, получит новый id при
    /// каждой загрузке — но это не режим работы, а признак сломанного файла: рёбра в него всё
    /// равно уже ни во что не попадают, и валидатор об этом скажет.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Подпись ноды: то, что видно в полосе лога прогона, в панели переменных, в замечаниях
    /// валидатора и в браузере шаблонов. Свободная строка, уникальность желательна, но не
    /// обязательна (дубликат — предупреждение валидатора, а не ошибка).
    ///
    /// Пустая строка законна и означает «имя не задано»; читатели зовут
    /// <see cref="MacroNodeNames.Display"/>, который подставляет вместо неё имя семейства ноды,
    /// — так правленный руками файл без этого поля не даёт пустых строк в логе.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Расположение в канве редактора. <c>null</c>, пока граф не открывали в визуальном редакторе.</summary>
    public NodeEditorInfo? Editor { get; init; }
}
