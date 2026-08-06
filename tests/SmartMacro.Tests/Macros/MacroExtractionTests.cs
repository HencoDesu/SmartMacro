using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// Выделение куска графа в под-макрос (F4) — и, в первую очередь, ОТКАЗЫ.
///
/// Их здесь больше, чем успешных случаев, и это не перекос: решение волны состояло именно в том,
/// чтобы неоднозначное выделение отвергать с объяснением, а не достраивать догадкой. Каждый отказ
/// проверяется вместе с тем, что он НАЗЫВАЕТ ноды: «выделение не извлекается» не говорит
/// пользователю, что делать дальше, а «два входа: click-3, find-7» говорит.
/// </summary>
public class MacroExtractionTests
{
    private static KeyPressNode Key(string id, Guid? next = null) =>
        new() { Id = Ids.Of(id), DisplayName = id, Key = VirtualKey.F1, Next = next };

    private static FindElementNode Find(string id, Guid? found, Guid? notFound) =>
        new() { Id = Ids.Of(id), DisplayName = id, Template = "т", Found = found, NotFound = notFound };

    // a → b → c → d
    private static MacroGraph Chain() => new()
    {
        Name = "цепочка",
        StartNodeId = Ids.Of("a"),
        Nodes =
        [
            Key("a", Ids.Of("b")),
            Key("b", Ids.Of("c")),
            Key("c", Ids.Of("d")),
            Key("d"),
        ],
    };

    [Test]
    public async Task AMiddleChunk_BecomesAFunctionCalledFromWhereItWas()
    {
        var result = MacroExtraction.Extract(Chain(), [Ids.Of("b"), Ids.Of("c")], "функция-1");

        await Assert.That(result.Refusal).IsNull();

        // Родитель: a → вызов → d. Кусок вырезан, ребро в его вход переехало на ноду вызова, а
        // единственный выход стал её Next.
        var parent = result.Parent!;
        await Assert.That(parent.Nodes.Select(MacroNodeNames.Display)).IsEquivalentTo(new[] { "a", "sub-1", "d" });
        var call = parent.Nodes.OfType<RunSubmacroNode>().Single();
        await Assert.That(((KeyPressNode)parent.Nodes[0]).Next).IsEqualTo(call.Id);
        await Assert.That(call.Next).IsEqualTo(Ids.Of("d"));
        await Assert.That(call.SubmacroId).IsEqualTo(result.Submacro!.Id);
        await Assert.That(call.Await).IsTrue();

        // Под-макрос: b → c → возврат. Ребро наружу стало null — это и есть «вернуть управление».
        var sub = result.Submacro!.Graph;
        await Assert.That(sub.Name).IsEqualTo("функция-1");
        await Assert.That(sub.Triggers).IsEmpty();
        await Assert.That(sub.StartNodeId).IsEqualTo(Ids.Of("b"));
        await Assert.That(sub.Nodes.Select(MacroNodeNames.Display)).IsEquivalentTo(new[] { "b", "c" });
        await Assert.That(((KeyPressNode)sub.Nodes[1]).Next).IsNull();
    }

    /// <summary>Хвост графа: выходов наружу нет вовсе, значит нода вызова заканчивает прогон.</summary>
    [Test]
    public async Task ATail_LeavesTheCallWithNoNext()
    {
        var result = MacroExtraction.Extract(Chain(), [Ids.Of("c"), Ids.Of("d")], "хвост");

        await Assert.That(result.Refusal).IsNull();
        await Assert.That(result.Parent!.Nodes.OfType<RunSubmacroNode>().Single().Next).IsNull();
    }

    /// <summary>Выделение с головы графа: стартовой нодой становится вызов.</summary>
    [Test]
    public async Task ExtractingTheHead_MovesTheStartNodeToTheCall()
    {
        var result = MacroExtraction.Extract(Chain(), [Ids.Of("a"), Ids.Of("b")], "голова");

        await Assert.That(result.Refusal).IsNull();
        await Assert.That(result.Parent!.StartNodeId).IsEqualTo(result.CallNodeId);
        await Assert.That(result.Submacro!.Graph.StartNodeId).IsEqualTo(Ids.Of("a"));
    }

    /// <summary>
    /// Два входа: снаружи ведут рёбра в две разные ноды выделения. Это две функции, слипшиеся в
    /// одну, и достроить их в одну можно только догадкой о намерении.
    /// </summary>
    [Test]
    public async Task TwoEntries_AreRefusedByName()
    {
        // find → (found: x, notFound: y); выделяем x и y.
        var graph = new MacroGraph
        {
            Name = "развилка",
            StartNodeId = Ids.Of("find"),
            Nodes = [Find("find", Ids.Of("x"), Ids.Of("y")), Key("x"), Key("y")],
        };

        var result = MacroExtraction.Extract(graph, [Ids.Of("x"), Ids.Of("y")], "ф");

        await Assert.That(result.IsOk).IsFalse();
        // Отказ существует ради того, чтобы НАЗВАТЬ виноватое: и сколько входов, и какие именно.
        var refusal = Msg.Args(result.Refusal, Strings.Extraction_Refused_ManyEntries);
        await Assert.That(refusal[0]).IsEqualTo("2");
        await Assert.That(refusal[1]).Contains("«x»");
        await Assert.That(refusal[1]).Contains("«y»");
    }

    /// <summary>Выходы в разные места: куда идти после функции, выбирать надо снаружи.</summary>
    [Test]
    public async Task DivergingExits_AreRefusedByName()
    {
        // a → find → (found: x, notFound: y); выделяем find.
        var graph = new MacroGraph
        {
            Name = "развилка",
            StartNodeId = Ids.Of("a"),
            Nodes = [Key("a", Ids.Of("find")), Find("find", Ids.Of("x"), Ids.Of("y")), Key("x"), Key("y")],
        };

        var result = MacroExtraction.Extract(graph, [Ids.Of("find")], "ф");

        await Assert.That(result.IsOk).IsFalse();
        await Assert.That(Msg.Arg(result.Refusal, Strings.Extraction_Refused_ManyExits)).Contains("«x»");
    }

    /// <summary>
    /// Часть выходов кончает прогон, часть уходит наружу. После извлечения оба стали бы одним
    /// возвратом — то есть ветка, которая раньше прогон ЗАКАНЧИВАЛА, начала бы его продолжать.
    /// Молча менять поведение ветки, дорисованной автором специально, нельзя.
    /// </summary>
    [Test]
    public async Task AnExitMixedWithEndOfRun_IsRefused()
    {
        var graph = new MacroGraph
        {
            Name = "развилка",
            StartNodeId = Ids.Of("a"),
            Nodes = [Key("a", Ids.Of("find")), Find("find", Ids.Of("x"), null), Key("x")],
        };

        var result = MacroExtraction.Extract(graph, [Ids.Of("find")], "ф");

        await Assert.That(result.IsOk).IsFalse();
        // Именно смешанный случай, а не «выходы в разные места»: разница в том, что одна из
        // веток прогон ЗАКАНЧИВАЛА, и после извлечения продолжила бы.
        await Assert.That(Msg.Arg(result.Refusal, Strings.Extraction_Refused_MixedExits)).Contains("«x»");
        await Assert.That(Msg.Is(result.Refusal, Strings.Extraction_Refused_ManyExits)).IsFalse();
    }

    [Test]
    public async Task ANodeThatAlreadyCallsAFunction_CannotBeExtracted()
    {
        var graph = new MacroGraph
        {
            Name = "м",
            StartNodeId = Ids.Of("a"),
            Nodes =
            [
                Key("a", Ids.Of("call")),
                new RunSubmacroNode { Id = Ids.Of("call"), DisplayName = "call", SubmacroId = Guid.NewGuid() },
            ],
        };

        var result = MacroExtraction.Extract(graph, [Ids.Of("call")], "ф");

        await Assert.That(result.IsOk).IsFalse();
        await Assert.That(Msg.Is(result.Refusal, Strings.Extraction_Refused_ContainsCall)).IsTrue();
    }

    [Test]
    public async Task ExtractingEverything_IsRefused()
    {
        var result = MacroExtraction.Extract(
            Chain(),
            [Ids.Of("a"), Ids.Of("b"), Ids.Of("c"), Ids.Of("d")],
            "всё");

        await Assert.That(result.IsOk).IsFalse();
        await Assert.That(Msg.Is(result.Refusal, Strings.Extraction_Refused_WholeGraph)).IsTrue();
    }

    [Test]
    public async Task AnEmptySelection_IsRefused()
    {
        var result = MacroExtraction.Extract(Chain(), [], "ф");

        await Assert.That(result.IsOk).IsFalse();
        await Assert.That(Msg.Is(result.Refusal, Strings.Extraction_Refused_NothingSelected)).IsTrue();
    }

    /// <summary>
    /// Извлечение не мутирует исходный граф: редактор на отказе обязан не применить НИЧЕГО, а на
    /// успехе — заменить содержимое канвы целиком.
    /// </summary>
    [Test]
    public async Task TheSourceGraphIsLeftUntouched()
    {
        var graph = Chain();
        var before = MacroGraphJson.Serialize(graph);

        MacroExtraction.Extract(graph, [Ids.Of("b"), Ids.Of("c")], "ф");

        await Assert.That(MacroGraphJson.Serialize(graph)).IsEqualTo(before);
    }

    /// <summary>Нода вызова встаёт туда, где стоял вход: чтение графа не должно поехать оттого, что кусок свернули.</summary>
    [Test]
    public async Task TheCallNodeInheritsThePositionOfTheEntry()
    {
        var graph = Chain();
        graph.Nodes[1] = ((KeyPressNode)graph.Nodes[1]) with { Editor = new NodeEditorInfo(120, 240) };

        var result = MacroExtraction.Extract(graph, [Ids.Of("b"), Ids.Of("c")], "ф");

        await Assert.That(result.Parent!.Nodes.OfType<RunSubmacroNode>().Single().Editor)
            .IsEqualTo(new NodeEditorInfo(120, 240));
    }
}
