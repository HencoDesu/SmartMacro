using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Validation;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

/// <summary>
/// Под-макросы в бандле (F4): раскладка внутри файла, круг «записать → прочитать» и правила,
/// которые валидатор обязан держать за них.
/// </summary>
public class SubmacroBundleTests
{
    private static MacroSubmacro Sub(string name, string id = "s1", params MacroNode[] nodes)
    {
        var body = nodes.Length > 0 ? nodes : [new DelayNode { Id = Ids.Of("d"), DisplayName = "d", Ms = 1 }];
        return new MacroSubmacro(
            Ids.Of(id),
            new MacroGraph { Name = name, StartNodeId = body[0].Id, Nodes = [.. body] });
    }

    private static MacroGraph Parent(params MacroNode[] nodes) => new()
    {
        Name = "родитель",
        StartNodeId = nodes[0].Id,
        Nodes = [.. nodes],
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-f4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- раскладка внутри бандла --------------------------------------------------------

    [Test]
    public async Task ASubmacroPathIsItsGuid_AndNothingElseCounts()
    {
        var id = Guid.NewGuid();

        await Assert.That(MacroBundleFormat.TryParseSubmacroPath(MacroBundleFormat.SubmacroPath(id), out var parsed))
            .IsTrue();
        await Assert.That(parsed).IsEqualTo(id);

        // Вложенности нет, расширение одно, а имя, не разбирающееся в guid, — не под-макрос.
        await Assert.That(MacroBundleFormat.TryParseSubmacroPath($"куски/{id:D}.json", out _)).IsFalse();
        await Assert.That(MacroBundleFormat.TryParseSubmacroPath($"{id:D}.txt", out _)).IsFalse();
        await Assert.That(MacroBundleFormat.TryParseSubmacroPath("readme.json", out _)).IsFalse();
        // Фигурные скобки Guid.TryParse проглотил бы, а писатель их не производит: два написания
        // одной личности — это два файла на месте одного.
        await Assert.That(MacroBundleFormat.TryParseSubmacroPath($"{{{id:D}}}.json", out _)).IsFalse();
        await Assert.That(MacroBundleFormat.TryParseSubmacroPath($"{Guid.Empty:D}.json", out _)).IsFalse();
    }

    [Test]
    public async Task SubmacrosSurviveTheSaveReadRoundTrip()
    {
        var dir = TempDir();
        try
        {
            var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = Ids.Of("s1") };
            MacroBundleFolder.Save(dir, Parent(call), [Sub("опознать")]);

            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "родитель"));

            await Assert.That(entry.Submacros).Count().IsEqualTo(1);
            await Assert.That(entry.Submacros[0].Id).IsEqualTo(Ids.Of("s1"));
            await Assert.That(entry.Submacros[0].Name).IsEqualTo("опознать");
            await Assert.That(entry.SubmacroFaults).IsEmpty();
            await Assert.That(entry.Validate()).IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>null</c> означает «оставить те, что в файле», пустой список — «их больше нет». Разница
    /// несущая: правку шаблона делает тот, кто про под-макросы не знает, и стереть их за него
    /// значило бы потерять работу молча.
    /// </summary>
    [Test]
    public async Task SavingWithoutSubmacros_KeepsThemButAnEmptyListRemovesThem()
    {
        var dir = TempDir();
        try
        {
            var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = Ids.Of("s1") };
            MacroBundleFolder.Save(dir, Parent(call), [Sub("опознать")]);

            MacroBundleFolder.Save(dir, Parent(call));
            await Assert.That(MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "родитель")).Submacros)
                .Count().IsEqualTo(1);

            MacroBundleFolder.Save(dir, Parent(call), []);
            await Assert.That(MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "родитель")).Submacros)
                .IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Переименование макроса переносит под-макросы вместе с шаблонами — это тот же
    /// <c>renamedFrom</c>, и забыть про них здесь значило бы потерять весь смысл формата.
    /// </summary>
    [Test]
    public async Task RenamingCarriesTheSubmacrosOver()
    {
        var dir = TempDir();
        try
        {
            var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = Ids.Of("s1") };
            MacroBundleFolder.Save(dir, Parent(call), [Sub("опознать")]);

            var renamed = new MacroGraph { Name = "новое", StartNodeId = call.Id, Nodes = [call] };
            MacroBundleFolder.Save(dir, renamed, submacros: null, renamedFrom: "родитель");

            var entry = MacroBundleFolder.ReadEntry(MacroBundleFolder.PathFor(dir, "новое"));
            await Assert.That(entry.Submacros.Select(s => s.Name)).IsEquivalentTo(new[] { "опознать" });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- правила валидатора --------------------------------------------------------------

    [Test]
    public async Task ATriggerInsideASubmacroIsAnError()
    {
        var sub = Sub("опознать") with { };
        var withTrigger = new MacroSubmacro(
            sub.Id,
            new MacroGraph
            {
                Name = sub.Name,
                Triggers = [new HotkeyTrigger(HotkeyModifiers.Control, VirtualKey.F1)],
                StartNodeId = sub.Graph.StartNodeId,
                Nodes = sub.Graph.Nodes,
            });
        var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = sub.Id };

        var issues = MacroGraphValidator.ValidateBundle(Parent(call), [withTrigger]);

        await Assert.That(issues.Any(i => i.Severity == ValidationSeverity.Error
                                          && i.SubmacroId == sub.Id
                                          && i.Message.Contains("триггер"))).IsTrue();
    }

    [Test]
    public async Task ASubmacroCallingASubmacroIsAnError()
    {
        var inner = Sub("внутренняя", "s2");
        var outer = new MacroSubmacro(
            Ids.Of("s1"),
            new MacroGraph
            {
                Name = "внешняя",
                StartNodeId = Ids.Of("call2"),
                Nodes = [new RunSubmacroNode { Id = Ids.Of("call2"), DisplayName = "call2", SubmacroId = inner.Id }],
            });
        var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = outer.Id };

        var issues = MacroGraphValidator.ValidateBundle(Parent(call), [outer, inner]);

        await Assert.That(issues.Any(i => i.Severity == ValidationSeverity.Error
                                          && i.SubmacroId == outer.Id
                                          && i.Message.Contains("вложенность плоская"))).IsTrue();
    }

    [Test]
    public async Task AReferenceToAMissingSubmacroIsAnError()
    {
        var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = Guid.NewGuid() };

        var issues = MacroGraphValidator.ValidateBundle(Parent(call), []);

        await Assert.That(issues.Any(i => i.Severity == ValidationSeverity.Error
                                          && i.NodeId == call.Id
                                          && i.Message.Contains("Под-макроса"))).IsTrue();
    }

    [Test]
    public async Task AnUnchosenSubmacroSaysSo_NotThatItIsMissing()
    {
        var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = Guid.Empty };

        var issues = MacroGraphValidator.ValidateBundle(Parent(call), []);

        await Assert.That(issues.Any(i => i.Message == "Под-макрос не выбран.")).IsTrue();
    }

    /// <summary>
    /// Живой пример из спеки: <c>RecognizeTag</c> внутри функции, <c>SetIcon</c> снаружи.
    /// Молчаливая потеря значения здесь недопустима — подпрогон работает с КОПИЕЙ переменных, и
    /// читающая нода родителя оборвёт прогон.
    /// </summary>
    [Test]
    public async Task AVariableWrittenInsideAndReadOutside_IsAWarningOnTheCall()
    {
        var sub = new MacroSubmacro(
            Ids.Of("s1"),
            new MacroGraph
            {
                Name = "опознать",
                StartNodeId = Ids.Of("rec"),
                Nodes =
                [
                    new RecognizeTagNode
                    {
                        Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "classes",
                        Region = new ScreenRect(0, 0, 10, 10), ResultVar = "tag",
                    },
                ],
            });
        var call = new RunSubmacroNode
        {
            Id = Ids.Of("c"), DisplayName = "c", SubmacroId = sub.Id, Next = Ids.Of("icon"),
        };
        var icon = new SetIconNode { Id = Ids.Of("icon"), DisplayName = "icon", IconPath = "x/{tag}.png" };

        var issues = MacroGraphValidator.ValidateBundle(Parent(call, icon), [sub]);

        var warning = issues.Single(i => i.NodeId == call.Id && i.Severity == ValidationSeverity.Warning);
        await Assert.That(warning.Message).Contains("«tag»");
        await Assert.That(warning.Message).Contains("КОПИЕЙ");
    }

    /// <summary>
    /// А когда переменную пишет и сам родитель, предупреждения быть НЕ ДОЛЖНО: значение у него
    /// своё. Предупреждать здесь значило бы приучить не читать предупреждения.
    /// </summary>
    [Test]
    public async Task AVariableTheParentWritesItself_IsNotWarnedAbout()
    {
        var sub = new MacroSubmacro(
            Ids.Of("s1"),
            new MacroGraph
            {
                Name = "опознать",
                StartNodeId = Ids.Of("rec"),
                Nodes =
                [
                    new RecognizeTagNode
                    {
                        Id = Ids.Of("rec"), DisplayName = "rec", TemplateSet = "classes",
                        Region = new ScreenRect(0, 0, 10, 10), ResultVar = "tag",
                    },
                ],
            });
        var own = new RecognizeTagNode
        {
            Id = Ids.Of("own"), DisplayName = "own", TemplateSet = "classes",
            Region = new ScreenRect(0, 0, 10, 10), ResultVar = "tag", Matched = Ids.Of("c"),
        };
        var call = new RunSubmacroNode
        {
            Id = Ids.Of("c"), DisplayName = "c", SubmacroId = sub.Id, Next = Ids.Of("icon"),
        };
        var icon = new SetIconNode { Id = Ids.Of("icon"), DisplayName = "icon", IconPath = "x/{tag}.png" };

        var issues = MacroGraphValidator.ValidateBundle(Parent(own, call, icon), [sub]);

        await Assert.That(issues.Any(i => i.NodeId == call.Id)).IsFalse();
    }

    /// <summary>
    /// Ноды под-макроса проходят те же проверки, что и ноды родителя, и замечание про них несёт
    /// ТРЕТЬЮ КООРДИНАТУ — иначе редактор, открытый на родителе, искал бы такую ноду среди своих.
    /// </summary>
    [Test]
    public async Task IssuesInsideASubmacroCarryItsId()
    {
        var broken = new MacroSubmacro(
            Ids.Of("s1"),
            new MacroGraph
            {
                Name = "битая",
                StartNodeId = Ids.Of("k"),
                Nodes = [new KeyPressNode { Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.F1, Next = Ids.Of("призрак") }],
            });
        var call = new RunSubmacroNode { Id = Ids.Of("c"), DisplayName = "c", SubmacroId = broken.Id };

        var issues = MacroGraphValidator.ValidateBundle(Parent(call), [broken]);

        var issue = issues.Single(i => i.NodeId == Ids.Of("k"));
        await Assert.That(issue.SubmacroId).IsEqualTo(broken.Id);
        await Assert.That(issue.Severity).IsEqualTo(ValidationSeverity.Error);
    }
}
