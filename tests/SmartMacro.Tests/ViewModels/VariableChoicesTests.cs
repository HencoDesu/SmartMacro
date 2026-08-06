using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Подсказки для полей, где лежит ИМЯ переменной. Риск здесь не в том, что список пуст, а в том,
// что он предлагает не то: строку в поле точки — и макрос оборвётся на прогоне, получив вместо
// координат имя шаблона. Поэтому проверяется РАЗДЕЛЕНИЕ, а не наличие.
public class VariableChoicesTests
{
    private static MacroEditorViewModel Editor(TempLibrary library) =>
        new(new FakeIpcClient(), library.Library, null, null, ImmediateUiDispatcher.Instance);

    // Find пишет точку, «Сопоставить с набором» — строку, «Добавить тег» читает {класс}
    // подстановкой (то есть вид переменной остаётся неизвестным), Клик читает точку.
    private static MacroGraph Mixed(string name = "смесь") => new()
    {
        Name = name,
        StartNodeId = Ids.Of("find"),
        Nodes =
        [
            new FindElementNode
            {
                Id = Ids.Of("find"), DisplayName = "find-1", Template = "Btn",
                FoundPointVar = "кнопка", Found = Ids.Of("match"),
            },
            new MatchTemplateSetNode
            {
                Id = Ids.Of("match"), DisplayName = "match-2", TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 100, 100), ResultVar = "класс", Matched = Ids.Of("tag"),
            },
            new AddTagNode { Id = Ids.Of("tag"), DisplayName = "addtag-3", Tag = "{класс}", Next = Ids.Of("click") },
            new ClickNode { Id = Ids.Of("click"), DisplayName = "click-4", PointVar = "кнопка" },
        ],
    };

    [Test]
    public async Task PointFieldsAndTextFieldsGetDifferentLists()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Mixed());
        using var vm = Editor(library);
        vm.SelectedMacro = vm.Macros.Single();

        // cursor засевает триггер, «кнопка» пишет Find — обе точки.
        await Assert.That(vm.PointVariableChoices).Contains("кнопка");
        await Assert.That(vm.PointVariableChoices).Contains(MacroVariableNames.Cursor);
        // «класс» — строка, и в списке точек ему делать нечего: это ровно та подсказка, которая
        // помогла бы собрать оборвущийся макрос.
        await Assert.That(vm.PointVariableChoices).DoesNotContain("класс");

        await Assert.That(vm.TextVariableChoices).Contains("класс");
        await Assert.That(vm.TextVariableChoices).DoesNotContain("кнопка");
    }

    [Test]
    public async Task EachRowGetsTheListOfItsOwnKind()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Mixed());
        using var vm = Editor(library);
        vm.SelectedMacro = vm.Macros.Single();

        var find = (FindElementNodeRowViewModel)vm.Nodes.Single(n => n.DisplayName == "find-1");
        var match = (MatchTemplateSetNodeRowViewModel)vm.Nodes.Single(n => n.DisplayName == "match-2");
        var click = (ClickNodeRowViewModel)vm.Nodes.Single(n => n.DisplayName == "click-4");

        await Assert.That(find.VariableChoices).IsSameReferenceAs(vm.PointVariableChoices);
        await Assert.That(click.VariableChoices).IsSameReferenceAs(vm.PointVariableChoices);
        await Assert.That(match.VariableChoices).IsSameReferenceAs(vm.TextVariableChoices);
    }

    // Имя, которое только ЧИТАЮТ подстановкой и нигде не пишут, попадает в список строк — это и
    // есть тот случай, ради которого список нужнее всего: тег читается, а ноду, которая его
    // запишет, ещё предстоит завести.
    [Test]
    public async Task AnUnwrittenInterpolatedNameIsOfferedAsAText()
    {
        using var library = new TempLibrary();
        library.WriteExternally(new MacroGraph
        {
            Name = "только-чтение",
            StartNodeId = Ids.Of("tag"),
            Nodes = [new AddTagNode { Id = Ids.Of("tag"), DisplayName = "addtag-1", Tag = "{ничей}" }],
        });
        using var vm = Editor(library);
        vm.SelectedMacro = vm.Macros.Single();

        await Assert.That(vm.TextVariableChoices).Contains("ничей");
        await Assert.That(vm.PointVariableChoices).DoesNotContain("ничей");
    }

    // Набор имени в поле обязан пополнить список ТУТ ЖЕ: иначе только что заведённую переменную
    // пришлось бы набирать во второй ноде руками, то есть подсказка не работала бы ровно в том
    // сценарии, ради которого она заведена. FoundPointVar в Summary не входит, поэтому событие
    // приходит своим именем свойства.
    [Test]
    public async Task TypingANewNameAddsItToTheListImmediately()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Mixed());
        using var vm = Editor(library);
        vm.SelectedMacro = vm.Macros.Single();
        var find = (FindElementNodeRowViewModel)vm.Nodes.Single(n => n.DisplayName == "find-1");

        find.FoundPointVar = "новая-точка";

        await Assert.That(vm.PointVariableChoices).Contains("новая-точка");
    }

    // Под-прогон получает КОПИЮ переменных родителя, поэтому читать их внутри функции законно —
    // и не предложить их значило бы заставить набирать руками то, что и так работает.
    [Test]
    public async Task InsideASubmacroTheParentNamesAreOfferedToo()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Mixed());
        using var vm = Editor(library);
        vm.SelectedMacro = vm.Macros.Single();

        var id = vm.AddSubmacro("функция");
        await Assert.That(vm.OpenSubmacro(id)).IsTrue();

        // Свой граф пуст, но родительские имена видны.
        await Assert.That(vm.PointVariableChoices).Contains("кнопка");
        await Assert.That(vm.TextVariableChoices).Contains("класс");
    }

    // Возврат к родителю не имеет права оставить имена дважды: список общий и живёт столько же,
    // сколько редактор.
    [Test]
    public async Task ComingBackFromASubmacroDoesNotDuplicateNames()
    {
        using var library = new TempLibrary();
        library.WriteExternally(Mixed());
        using var vm = Editor(library);
        vm.SelectedMacro = vm.Macros.Single();

        var id = vm.AddSubmacro("функция");
        vm.OpenSubmacro(id);
        vm.OpenParentGraph();

        await Assert.That(vm.PointVariableChoices.Count(n => n == "кнопка")).IsEqualTo(1);
        await Assert.That(vm.TextVariableChoices.Count(n => n == "класс")).IsEqualTo(1);
    }
}
