using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// Правило автогенерации подписи. Отдельным набором — потому что читаемость лога это ВСЁ, ради
// чего DisplayName существует: связь идёт по Guid, и если имена по умолчанию снова станут
// «n1»/«n2», поле окажется чистым накладным расходом.
public class MacroNodeNamesTests
{
    [Test]
    public async Task Generate_NamesNodesAfterTheirFamily_WithAGraphWideNumber()
    {
        // Номер СКВОЗНОЙ, а не свой у каждого семейства: «click-1, delay-2, find-3» заодно
        // читается как порядок, в котором ноды заводили, а «click-1, delay-1, find-1» не
        // сообщает ничего.
        var used = new List<string>();
        foreach (MacroNode node in new MacroNode[]
                 {
                     new ClickNode { Point = default(ScreenPoint) },
                     new DelayNode { Ms = 1 },
                     new FindElementNode { Template = "т" },
                 })
        {
            used.Add(MacroNodeNames.Generate(MacroNodeNames.Prefix(node), used));
        }

        await Assert.That(used).IsEquivalentTo(new List<string> { "click-1", "delay-2", "find-3" });
    }

    [Test]
    public async Task Generate_ReusesTheSmallestFreeNumber_SoDeletingDoesNotLeaveAHoleForever()
    {
        var name = MacroNodeNames.Generate("key", ["click-1", "find-3"]);

        await Assert.That(name).IsEqualTo("key-2");
    }

    [Test]
    public async Task Generate_IgnoresHandTypedNamesWithoutANumericTail()
    {
        // «Открыть характеристики» номера не занимает: иначе первая же нода, названная руками,
        // начала бы двигать нумерацию всех последующих неизвестно куда.
        var name = MacroNodeNames.Generate("click", ["Открыть характеристики", "проверка-класса"]);

        await Assert.That(name).IsEqualTo("click-1");
    }

    [Test]
    public async Task Generate_SkipsANameThatIsAlreadyTakenWholesale()
    {
        // Номер 1 свободен (у «click-1» его занял бы только хвост), но само имя занято — значит,
        // идём дальше, а не выдаём дубликат.
        var name = MacroNodeNames.Generate("click", ["click-1"]);

        await Assert.That(name).IsEqualTo("click-2");
    }

    [Test]
    public async Task Display_FallsBackToTheFamilyName_WhenTheFileCarriesNoLabel()
    {
        // Правленный руками файл вправе не нести подписи вовсе — пустая ячейка в колонке имени
        // читалась бы как пропущенная строка лога.
        var node = new MatchTemplateSetNode { TemplateSet = "classes", Region = default };

        await Assert.That(MacroNodeNames.Display(node)).IsEqualTo("match");
        await Assert.That(MacroNodeNames.Display(node with { DisplayName = "класс" })).IsEqualTo("класс");
    }

    [Test]
    public async Task EveryNodeKind_HasItsOwnPrefix()
    {
        // Одинаковый префикс у двух семейств означал бы, что подпись по умолчанию перестала
        // говорить, ЧТО нода делает, — а это и есть её единственная работа.
        MacroNode[] all =
        [
            new KeyPressNode { Key = VirtualKey.F1 },
            new ClickNode { Point = default(ScreenPoint) },
            new DelayNode { Ms = 1 },
            new AddTagNode { Tag = "т" },
            new RemoveTagNode { Tag = "т" },
            new SetIconNode { IconPath = "и" },
            new RunSubmacroNode { SubmacroId = Guid.NewGuid() },
            new FindElementNode { Template = "т" },
            new WaitForElementNode { Template = "т", TimeoutMs = 1 },
            new MatchTemplateSetNode { TemplateSet = "с", Region = default },
        ];

        var prefixes = all.Select(MacroNodeNames.Prefix).ToList();

        await Assert.That(prefixes.Distinct().Count()).IsEqualTo(all.Length);
        await Assert.That(prefixes.All(p => p.Length > 0)).IsTrue();
    }
}
