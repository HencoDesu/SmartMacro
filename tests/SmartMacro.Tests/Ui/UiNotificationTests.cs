using System.Globalization;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SmartMacro.App.ViewModels;

namespace SmartMacro.Tests.Ui;

/// <summary>
/// Правило про НЕПОДНЯТЫЙ <c>PropertyChanged</c> — тот класс дефектов, где значение верно, а на
/// экране его нет.
///
/// <b>Как оно устроено.</b> Панель проводят через настоящий сценарий (создать макрос, отметить
/// ноды, извлечь функцию, войти в неё и выйти), снимают состояние всего видимого дерева, затем
/// ПЕРЕПРИВЯЗЫВАЮТ окно — <c>DataContext</c> в <c>null</c> и обратно, — и снимают состояние
/// снова. Переприпривязка заставляет каждую привязку перечитать источник заново, то есть
/// показывает то, что панель показала БЫ, если бы все уведомления дошли. Расхождение двух
/// снимков и есть список свойств, о смене которых никто не сказал.
///
/// <b>Почему это правило, а не тест на конкретный дефект.</b> Волна F4 нашла глазами три таких
/// штуки за раз: раздел «Триггеры» оставался мёртвым после создания макроса (<c>ShowsTriggers</c>
/// считался верно, но не поднимался при смене <c>HasOpenMacro</c>); кнопка извлечения висела со
/// старым числом; выпадающий список ноды вызова был пуст при первом открытии. Все три — одна и
/// та же ошибка, и ловится она одним замером, потому что вопрос у неё один: «отличается ли
/// нарисованное от того, что показала бы честная перерисовка».
///
/// ⚠️ Правило видит только то, что попало в дерево: свойство, которое ни к чему не привязано,
/// молчит здесь так же, как и на экране. И оно не заменяет модульные тесты view-model — те
/// говорят, ЧТО должно быть, а этот говорит, что показанное этому соответствует.
/// </summary>
public partial class UiNotificationTests
{
    [Test]
    public async Task RebindingTheWindowChangesNothingOnScreen()
    {
        var problems = await Ui.RunAsync<IReadOnlyList<string>>(() =>
        {
            // Своя сцена, и НЕ с открытым макросом: сценарий начинается с «создать», а этот
            // переход существует только тогда, когда до него ничего открыто не было.
            using var scene = UiScene.Create(openMacro: false);
            return WalkThroughTheEditor(scene);
        });

        if (problems.Count > 0)
        {
            Assert.Fail(
                "После переприпривязки окна на экране изменилось то, что меняться не должно было." +
                NL + NL +
                "Это значит, что какое-то свойство view-model поменялось, а PropertyChanged о нём " +
                "поднят не был: привязка держала старое значение до тех пор, пока её не заставили " +
                "перечитать источник. На экране это выглядит как мёртвый раздел, пустой список или " +
                "счётчик со вчерашним числом — и ни сборка, ни модульные тесты этого не видят, " +
                "потому что само значение верное." + NL + NL +
                "Лечится в view-model: у вычисляемого свойства должен быть OnPropertyChanged из " +
                "сеттера того свойства, от которого оно вычисляется." + NL + NL +
                "Найдено:" + NL + string.Join(NL, problems));
        }

        await Assert.That(problems.Count).IsEqualTo(0);
    }

    // ---- сценарий ----------------------------------------------------------------------------

    private static string NL => Environment.NewLine;

    /// <summary>
    /// Прогоняет редактор по тем переходам, на которых уведомления и терялись, и сверяет экран
    /// с честной перерисовкой ПОСЛЕ КАЖДОГО.
    ///
    /// Сверять только в конце было бы недостаточно, и это выяснилось на опыте: раздел «Триггеры»
    /// не оживал именно после СОЗДАНИЯ макроса, а следующее же открытие макроса из библиотеки
    /// поднимало <c>ShowsTriggers</c> по другому пути и заметало дефект. Каждый переход
    /// проверяется там, где он произошёл.
    /// </summary>
    private static IReadOnlyList<string> WalkThroughTheEditor(UiScene scene)
    {
        var editor = scene.Editor;
        var problems = new List<string>();

        scene.Select(ShellMode.Macros);

        Checkpoint(scene, problems, "создали макрос", () => editor.NewMacro());

        Checkpoint(scene, problems, "открыли макрос из библиотеки",
            () => editor.SelectedMacro = editor.Macros.First(row => row.Name == "pw-boot"));

        Checkpoint(scene, problems, "отметили две ноды", () =>
        {
            editor.ToggleMark(editor.Nodes.First(node => node.DisplayName == "клик-2"));
            editor.ToggleMark(editor.Nodes.First(node => node.DisplayName == "пауза-3"));
        });

        Checkpoint(scene, problems, "извлекли под-макрос", () => editor.ExtractSubmacro("вынесенное"));

        Checkpoint(scene, problems, "выбрали ноду вызова",
            () => editor.SelectedNode = editor.Nodes.FirstOrDefault(node => node.TypeLabel == "Под-макрос"));

        Checkpoint(scene, problems, "вошли в функцию", () =>
        {
            var submacro = editor.Submacros.FirstOrDefault();
            if (submacro is not null)
            {
                editor.OpenSubmacro(submacro.Id);
            }
        });

        Checkpoint(scene, problems, "вернулись к родителю", () => editor.OpenParentGraph());

        return problems;
    }

    /// <summary>
    /// Выполняет шаг, снимает экран, переприпривязывает окно и снимает снова. Расхождение —
    /// свойство, о смене которого не сказали.
    /// </summary>
    private static void Checkpoint(UiScene scene, List<string> problems, string what, Action step)
    {
        step();
        UiScene.Settle();

        var before = Snapshot(scene.Window);

        var context = scene.Window.DataContext;
        scene.Window.DataContext = null;
        UiScene.Settle();
        scene.Window.DataContext = context;
        UiScene.Settle();

        problems.AddRange(Compare(before, Snapshot(scene.Window)).Select(line => $"  {what}:{line}"));
    }

    // ---- снимок дерева -----------------------------------------------------------------------

    /// <summary>
    /// Что видно и что напечатано — по адресу каждого узла, БЕЗ номеров одинаковых соседей.
    ///
    /// ⚠️ Номера убраны намеренно. <c>VirtualizingStackPanel</c> переиспользует контейнеры, и
    /// после смены <c>DataContext</c> те же четыре строки рейки законно приезжают в другие
    /// контейнеры: «Окна» оказывается там, где было «Лог». Сравнение по номерам объявило бы это
    /// потерянным уведомлением — четыре раза подряд. Значения одного адреса поэтому сравниваются
    /// как МНОЖЕСТВО: «эта строка поменяла текст» так по-прежнему видно, а «строки переехали
    /// между контейнерами» — уже нет.
    /// </summary>
    private static Dictionary<string, List<string>> Snapshot(Window window)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Walk(window, "окно", result);

        foreach (var values in result.Values)
        {
            values.Sort(StringComparer.Ordinal);
        }

        return result;
    }

    private static void Walk(Avalonia.Visual node, string path, Dictionary<string, List<string>> result)
    {
        if (node is Control control)
        {
            var text = UiTree.TextOf(control);
            if (text.Length > 0 && !IsClock(text))
            {
                Add(result, path, text);
            }

            if (control is ItemsControl items)
            {
                Add(result, path + "#штук", items.ItemCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (!node.IsVisible)
        {
            // Скрытую ветку не измеряют вовсе, так что сравнивать в ней нечего. Сам факт
            // скрытости уже записан адресом: у видимой ветки появятся дети, у скрытой — нет.
            return;
        }

        foreach (var child in node.GetVisualChildren())
        {
            Walk(child, $"{path}/{child.GetType().Name}", result);
        }
    }

    /// <summary>
    /// Живые часы: «1:13» в полосе прогона, «0:04» у отладчика, «12:21:39.137» в логе.
    ///
    /// ⚠️ Единственное исключение снимка, и оно вынужденное. Время «сколько уже идёт»
    /// вычисляется от <c>DateTimeOffset.Now</c> в момент отрисовки, поэтому между двумя
    /// снимками оно законно меняется — просто потому, что прошла секунда. Пойманный на этом тест
    /// падал примерно в двух прогонах из пяти, и падал НЕ по делу. Всё прочее в полосе прогона
    /// (имя макроса, текущая нода, строка простоя) в снимок входит.
    /// </summary>
    private static bool IsClock(string text) => ClockLike().IsMatch(text.Trim());

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?(\.\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex ClockLike();

    private static void Add(Dictionary<string, List<string>> result, string path, string value)
    {
        if (!result.TryGetValue(path, out var values))
        {
            values = [];
            result[path] = values;
        }

        values.Add(value);
    }

    private static IReadOnlyList<string> Compare(
        IReadOnlyDictionary<string, List<string>> before,
        IReadOnlyDictionary<string, List<string>> after)
    {
        var problems = new List<string>();

        foreach (var (path, values) in after)
        {
            if (!before.TryGetValue(path, out var was))
            {
                problems.Add($"  появилось после переприпривязки: {Describe(values)} ({path})");
            }
            else if (!was.SequenceEqual(values, StringComparer.Ordinal))
            {
                problems.Add($"  было {Describe(was)}, после переприпривязки {Describe(values)} ({path})");
            }
        }

        foreach (var (path, values) in before)
        {
            if (!after.ContainsKey(path))
            {
                problems.Add($"  пропало после переприпривязки: {Describe(values)} ({path})");
            }
        }

        return problems;
    }

    private static string Describe(IReadOnlyList<string> values) =>
        values.Count == 1 ? $"«{values[0]}»" : $"[{string.Join(", ", values.Select(v => $"«{v}»"))}]";
}
