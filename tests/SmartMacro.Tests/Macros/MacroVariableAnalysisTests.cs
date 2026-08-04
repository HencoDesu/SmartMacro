using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// D5: статическая половина панели переменных — кто каждую переменную пишет и кто её читает.
//
// Работа исключительно с моделью, поэтому проверяется без обхода, без окна и без трубы. Ради
// этого её и положили в Shared: панели нужен тот же ответ, какой дал бы демон, а
// единственный способ в этом убедиться — иметь одну реализацию и ловить её ошибки здесь, а не
// разглядывая нарисованный список.
public class MacroVariableAnalysisTests
{
    private static MacroGraph Graph(params MacroNode[] nodes) =>
        new() { Name = "тест", StartNodeId = nodes.Length > 0 ? nodes[0].Id : Ids.Of("n1"), Nodes = [.. nodes] };

    private static MacroVariableInfo Var(MacroGraph graph, string name) =>
        MacroVariableAnalysis.Analyze(graph).Single(v => v.Name == name);

    // ---- пример из самого макета -----------------------------------------------------

    [Test]
    public async Task RecognizeWritesTag_AndSetIconReadsItOutOfTheIconPath()
    {
        // В точности панель переменных из макета 1d: «{tag} · строка · пишет recognize-class ·
        // читает set-icon (в пути к иконке)».
        var graph = Graph(
            new RecognizeTagNode
            {
                Id = Ids.Of("recognize-class"), DisplayName = "recognize-class",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 160, 35),
                Matched = Ids.Of("set-icon"),
            },
            new SetIconNode { Id = Ids.Of("set-icon"), DisplayName = "set-icon", IconPath = "Assets/ClassIcons/{tag}.png" });

        var tag = Var(graph, "tag");

        await Assert.That(tag.Kind).IsEqualTo(VariableKind.Text);
        await Assert.That(tag.SeededByTrigger).IsFalse();
        await Assert.That(tag.Writes.Select(w => w.NodeName)).IsEquivalentTo(new[] { "recognize-class" });
        await Assert.That(tag.Writes[0].Slot).IsEqualTo(VariableSlot.ResultVar);
        await Assert.That(tag.Reads.Select(r => r.NodeName)).IsEquivalentTo(new[] { "set-icon" });
        await Assert.That(tag.Reads[0].Slot).IsEqualTo(VariableSlot.IconPath);
    }

    [Test]
    public async Task TagComesBeforeCursor_BecauseSomethingActuallyWritesIt()
    {
        var graph = Graph(
            new RecognizeTagNode { Id = Ids.Of("recognize"), DisplayName = "recognize", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1) },
            new SetIconNode { Id = Ids.Of("icon"), DisplayName = "icon", IconPath = "{tag}.png" });

        // Порядок, в котором рисует панель: переменная с писателем сверху, всегда присутствующая
        // затравка от триггера под ней. Совпадает с макетом и ставит интересную первой.
        await Assert.That(MacroVariableAnalysis.Analyze(graph).Select(v => v.Name))
            .IsEquivalentTo(new[] { "tag", "cursor" });
    }

    // ---- cursor ----------------------------------------------------------------------

    [Test]
    public async Task CursorIsAlwaysListed_EvenWhenTheGraphNeverMentionsIt()
    {
        var graph = Graph(new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 10 });

        var cursor = Var(graph, MacroVariableNames.Cursor);

        await Assert.That(cursor.SeededByTrigger).IsTrue();
        await Assert.That(cursor.Kind).IsEqualTo(VariableKind.Point);
        await Assert.That(cursor.Writes).IsEmpty();
        await Assert.That(cursor.Reads).IsEmpty();
        // Кладётся затравкой на любом пути запуска, поэтому определена, хотя её не пишет ни одна
        // нода.
        await Assert.That(cursor.IsDefined).IsTrue();
    }

    [Test]
    public async Task CursorReadByAClick_IsStillTheTriggerSeed()
    {
        var graph = Graph(new ClickNode { Id = Ids.Of("click"), DisplayName = "click", PointVar = "cursor" });

        var cursor = Var(graph, "cursor");

        await Assert.That(cursor.SeededByTrigger).IsTrue();
        await Assert.That(cursor.Reads.Select(r => r.Slot)).IsEquivalentTo(new[] { VariableSlot.PointVar });
    }

    [Test]
    public async Task TheCursorNameMatchesTheOneTheExecutorSeeds()
    {
        // Две константы, одно значение — только этот псевдоним и мешает панели пометить как
        // «триггер (сид)» переменную, которую демон затравкой никогда не кладёт.
        await Assert.That(MacroVariables.CursorVariableName).IsEqualTo(MacroVariableNames.Cursor);
    }

    // ---- все писатели и все читатели ---------------------------------------------------

    [Test]
    public async Task FoundPointVarIsAPointWrite_OnBothConditionals()
    {
        var graph = Graph(
            new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "X", FoundPointVar = "here", Found = Ids.Of("wait") },
            new WaitForElementNode { Id = Ids.Of("wait"), DisplayName = "wait", Template = "Y", TimeoutMs = 1, FoundPointVar = "there" });

        await Assert.That(Var(graph, "here").Kind).IsEqualTo(VariableKind.Point);
        await Assert.That(Var(graph, "here").Writes[0].Slot).IsEqualTo(VariableSlot.FoundPointVar);
        await Assert.That(Var(graph, "there").Writes[0].Slot).IsEqualTo(VariableSlot.FoundPointVar);
    }

    [Test]
    public async Task EveryInterpolatedFieldIsARead_WithItsOwnSlot()
    {
        var graph = Graph(
            new AddTagNode { Id = Ids.Of("add"), DisplayName = "add", Tag = "{a}", Next = Ids.Of("remove") },
            new RemoveTagNode { Id = Ids.Of("remove"), DisplayName = "remove", Tag = "{b}", Next = Ids.Of("icon") },
            new SetIconNode { Id = Ids.Of("icon"), DisplayName = "icon", IconPath = "x/{c}.png", Next = Ids.Of("sub") },
            new RunMacroNode { Id = Ids.Of("sub"), DisplayName = "sub", MacroName = "pw-{d}" });

        await Assert.That(Var(graph, "a").Reads[0].Slot).IsEqualTo(VariableSlot.Tag);
        await Assert.That(Var(graph, "b").Reads[0].Slot).IsEqualTo(VariableSlot.Tag);
        await Assert.That(Var(graph, "c").Reads[0].Slot).IsEqualTo(VariableSlot.IconPath);
        await Assert.That(Var(graph, "d").Reads[0].Slot).IsEqualTo(VariableSlot.MacroName);
    }

    [Test]
    public async Task KeyAndDelayNodesTouchNothing()
    {
        var graph = Graph(
            new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.C, Next = Ids.Of("d") },
            new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 500 });

        // Только затравка от триггера. Имена шаблонов и имена клавиш подстановке НЕ подлежат
        // (спека §5.3), так что шаблон, буквально названный "{x}", не имеет права попасть в отчёт
        // как переменная.
        await Assert.That(MacroVariableAnalysis.Analyze(graph).Select(v => v.Name))
            .IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    public async Task ATemplateNameIsNotInterpolated_SoItIsNotARead()
    {
        var graph = Graph(new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "{tag}Button" });

        await Assert.That(MacroVariableAnalysis.Analyze(graph).Any(v => v.Name == "tag")).IsFalse();
    }

    // ---- честные случаи отказа ---------------------------------------------------------

    [Test]
    public async Task AReadWithNoWriterIsReportedAsUndefined()
    {
        var graph = Graph(new SetIconNode { Id = Ids.Of("icon"), DisplayName = "icon", IconPath = "{ghost}.png" });

        var ghost = Var(graph, "ghost");

        // Во время прогона это прерывает обход, а не подставляет пустоту (спека §5.3), — значит,
        // панель обязана уметь про это сказать.
        await Assert.That(ghost.IsDefined).IsFalse();
        await Assert.That(ghost.Kind).IsEqualTo(VariableKind.Unknown);
    }

    [Test]
    public async Task AWriteNobodyReadsIsReportedAsUnread()
    {
        var graph = Graph(new RecognizeTagNode { Id = Ids.Of("r"), DisplayName = "r", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1) });

        await Assert.That(Var(graph, "tag").IsRead).IsFalse();
    }

    [Test]
    public async Task SeveralReadersOfOneVariableAreAllListed()
    {
        var graph = Graph(
            new RecognizeTagNode { Id = Ids.Of("r"), DisplayName = "r", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1), Matched = Ids.Of("a") },
            new AddTagNode { Id = Ids.Of("a"), DisplayName = "a", Tag = "{tag}-готов", Next = Ids.Of("i") },
            new SetIconNode { Id = Ids.Of("i"), DisplayName = "i", IconPath = "{tag}.png" });

        await Assert.That(Var(graph, "tag").Reads.Select(r => r.NodeName)).IsEquivalentTo(new[] { "a", "i" });
    }

    [Test]
    public async Task TwoPlaceholdersInOneStringAreTwoVariables()
    {
        var graph = Graph(new SetIconNode { Id = Ids.Of("i"), DisplayName = "i", IconPath = "{dir}/{tag}.png" });

        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{dir}/{tag}.png"))
            .IsEquivalentTo(new[] { "dir", "tag" });
        await Assert.That(Var(graph, "dir").Reads).Count().IsEqualTo(1);
        await Assert.That(Var(graph, "tag").Reads).Count().IsEqualTo(1);
    }

    [Test]
    public async Task TheSameVariableTwiceInOneStringIsOneRead()
    {
        // Иначе панель написала бы «читает set-icon, set-icon».
        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{tag}/{tag}.png"))
            .IsEquivalentTo(new[] { "tag" });
    }

    [Test]
    public async Task BlankVariableNamesAreIgnored()
    {
        var graph = Graph(
            new RecognizeTagNode { Id = Ids.Of("r"), DisplayName = "r", TemplateSet = "s", Region = new ScreenRect(0, 0, 1, 1), ResultVar = "  " },
            new ClickNode { Id = Ids.Of("c"), DisplayName = "c", PointVar = string.Empty });

        await Assert.That(MacroVariableAnalysis.Analyze(graph).Select(v => v.Name))
            .IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    public async Task ThePlaceholderSyntaxIsTheOneTheExecutorSubstitutes()
    {
        // Регулярка та же самая, так что на деле проверяется, что её никто не перенабрал заново.
        // Сама сцепка тут важнее отдельных случаев: панель, заявляющая, что нода читает
        // переменную, которую исполнитель никогда не подставляет, — это ровно то же враньё, что и
        // бейдж целей из D4.
        var variables = new MacroVariables();
        variables.Set("tag", "Жрец");

        await Assert.That(variables.Interpolate("{tag}.png")).IsEqualTo("Жрец.png");
        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{tag}.png")).IsEquivalentTo(new[] { "tag" });

        // Экранирования нет, поэтому удвоенная скобка — это НЕ литеральная скобка: обе стороны
        // видят внутренний {tag} и внешние скобки не трогают. Закреплено потому, что "{{" выглядит
        // экранированием для всякого, кто встречал string.Format, — а панель обязана сообщать то,
        // что исполнитель сделает на самом деле, а не то, на что надеялся автор.
        await Assert.That(variables.Interpolate("{{tag}}")).IsEqualTo("{Жрец}");
        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{{tag}}")).IsEquivalentTo(new[] { "tag" });
    }
}
