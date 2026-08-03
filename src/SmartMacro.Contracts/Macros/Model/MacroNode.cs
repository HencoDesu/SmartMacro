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
    /// <summary>Уникальный в пределах графа id ноды. Рёбра ссылаются на ноды именно по нему.</summary>
    public required string Id { get; init; }

    /// <summary>Расположение в канве редактора. <c>null</c>, пока граф не открывали в визуальном редакторе.</summary>
    public NodeEditorInfo? Editor { get; init; }
}
