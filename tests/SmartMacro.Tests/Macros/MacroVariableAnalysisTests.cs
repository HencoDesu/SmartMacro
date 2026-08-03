using SmartMacro.Macros.Analysis;
using SmartMacro.Macros.Execution;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.Tests.Macros;

// D5: the static half of the variables panel — who writes each variable and who reads it.
//
// Pure model work, so it is tested without a walk, a window or a pipe. That is the point of
// putting it in Contracts: the panel needs the same answer the daemon would give, and the
// only way to be sure of that is for there to be one implementation and for its bugs to be
// findable here rather than by looking at a rendered list.
public class MacroVariableAnalysisTests
{
    private static MacroGraph Graph(params MacroNode[] nodes) =>
        new() { Name = "тест", StartNodeId = nodes.Length > 0 ? nodes[0].Id : "n1", Nodes = [.. nodes] };

    private static MacroVariableInfo Var(MacroGraph graph, string name) =>
        MacroVariableAnalysis.Analyze(graph).Single(v => v.Name == name);

    // ---- the mockup's own example ---------------------------------------------------

    [Test]
    public async Task RecognizeWritesTag_AndSetIconReadsItOutOfTheIconPath()
    {
        // Exactly mockup 1d's variables panel: «{tag} · строка · пишет recognize-class ·
        // читает set-icon (в пути к иконке)».
        var graph = Graph(
            new RecognizeTagNode
            {
                Id = "recognize-class",
                TemplateSet = "classes",
                Region = new ScreenRect(0, 0, 160, 35),
                Matched = "set-icon",
            },
            new SetIconNode { Id = "set-icon", IconPath = "Assets/ClassIcons/{tag}.png" });

        var tag = Var(graph, "tag");

        await Assert.That(tag.Kind).IsEqualTo(VariableKind.Text);
        await Assert.That(tag.SeededByTrigger).IsFalse();
        await Assert.That(tag.Writes.Select(w => w.NodeId)).IsEquivalentTo(new[] { "recognize-class" });
        await Assert.That(tag.Writes[0].Slot).IsEqualTo(VariableSlot.ResultVar);
        await Assert.That(tag.Reads.Select(r => r.NodeId)).IsEquivalentTo(new[] { "set-icon" });
        await Assert.That(tag.Reads[0].Slot).IsEqualTo(VariableSlot.IconPath);
    }

    [Test]
    public async Task TagComesBeforeCursor_BecauseSomethingActuallyWritesIt()
    {
        var graph = Graph(
            new RecognizeTagNode { Id = "recognize", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1) },
            new SetIconNode { Id = "icon", IconPath = "{tag}.png" });

        // The order the panel renders: the variable with a writer at the top, the always-there
        // trigger seed underneath. Matches the mockup and puts the interesting one first.
        await Assert.That(MacroVariableAnalysis.Analyze(graph).Select(v => v.Name))
            .IsEquivalentTo(new[] { "tag", "cursor" });
    }

    // ---- cursor ----------------------------------------------------------------------

    [Test]
    public async Task CursorIsAlwaysListed_EvenWhenTheGraphNeverMentionsIt()
    {
        var graph = Graph(new DelayNode { Id = "a", Ms = 10 });

        var cursor = Var(graph, MacroVariableNames.Cursor);

        await Assert.That(cursor.SeededByTrigger).IsTrue();
        await Assert.That(cursor.Kind).IsEqualTo(VariableKind.Point);
        await Assert.That(cursor.Writes).IsEmpty();
        await Assert.That(cursor.Reads).IsEmpty();
        // Seeded by every launch path, so it is defined even though no node writes it.
        await Assert.That(cursor.IsDefined).IsTrue();
    }

    [Test]
    public async Task CursorReadByAClick_IsStillTheTriggerSeed()
    {
        var graph = Graph(new ClickNode { Id = "click", PointVar = "cursor" });

        var cursor = Var(graph, "cursor");

        await Assert.That(cursor.SeededByTrigger).IsTrue();
        await Assert.That(cursor.Reads.Select(r => r.Slot)).IsEquivalentTo(new[] { VariableSlot.PointVar });
    }

    [Test]
    public async Task TheCursorNameMatchesTheOneTheExecutorSeeds()
    {
        // Two constants, one value — the alias is the only thing stopping the panel from
        // labelling a variable «триггер (сид)» that the daemon never seeds.
        await Assert.That(MacroVariables.CursorVariableName).IsEqualTo(MacroVariableNames.Cursor);
    }

    // ---- every writer and every reader ------------------------------------------------

    [Test]
    public async Task FoundPointVarIsAPointWrite_OnBothConditionals()
    {
        var graph = Graph(
            new FindElementNode { Id = "find", Template = "X", FoundPointVar = "here", Found = "wait" },
            new WaitForElementNode { Id = "wait", Template = "Y", TimeoutMs = 1, FoundPointVar = "there" });

        await Assert.That(Var(graph, "here").Kind).IsEqualTo(VariableKind.Point);
        await Assert.That(Var(graph, "here").Writes[0].Slot).IsEqualTo(VariableSlot.FoundPointVar);
        await Assert.That(Var(graph, "there").Writes[0].Slot).IsEqualTo(VariableSlot.FoundPointVar);
    }

    [Test]
    public async Task EveryInterpolatedFieldIsARead_WithItsOwnSlot()
    {
        var graph = Graph(
            new AddTagNode { Id = "add", Tag = "{a}", Next = "remove" },
            new RemoveTagNode { Id = "remove", Tag = "{b}", Next = "icon" },
            new SetIconNode { Id = "icon", IconPath = "x/{c}.png", Next = "sub" },
            new RunMacroNode { Id = "sub", MacroName = "pw-{d}" });

        await Assert.That(Var(graph, "a").Reads[0].Slot).IsEqualTo(VariableSlot.Tag);
        await Assert.That(Var(graph, "b").Reads[0].Slot).IsEqualTo(VariableSlot.Tag);
        await Assert.That(Var(graph, "c").Reads[0].Slot).IsEqualTo(VariableSlot.IconPath);
        await Assert.That(Var(graph, "d").Reads[0].Slot).IsEqualTo(VariableSlot.MacroName);
    }

    [Test]
    public async Task KeyAndDelayNodesTouchNothing()
    {
        var graph = Graph(
            new KeyPressNode { Id = "k", Key = VirtualKey.C, Next = "d" },
            new DelayNode { Id = "d", Ms = 500 });

        // Only the trigger seed. Template names and key names are NOT interpolated (spec
        // §5.3), so a template literally called "{x}" must not be reported as a variable.
        await Assert.That(MacroVariableAnalysis.Analyze(graph).Select(v => v.Name))
            .IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    public async Task ATemplateNameIsNotInterpolated_SoItIsNotARead()
    {
        var graph = Graph(new FindElementNode { Id = "find", Template = "{tag}Button" });

        await Assert.That(MacroVariableAnalysis.Analyze(graph).Any(v => v.Name == "tag")).IsFalse();
    }

    // ---- the honest failure cases ------------------------------------------------------

    [Test]
    public async Task AReadWithNoWriterIsReportedAsUndefined()
    {
        var graph = Graph(new SetIconNode { Id = "icon", IconPath = "{ghost}.png" });

        var ghost = Var(graph, "ghost");

        // At run time this aborts the walk rather than substituting blank (spec §5.3), so the
        // panel has to be able to say so.
        await Assert.That(ghost.IsDefined).IsFalse();
        await Assert.That(ghost.Kind).IsEqualTo(VariableKind.Unknown);
    }

    [Test]
    public async Task AWriteNobodyReadsIsReportedAsUnread()
    {
        var graph = Graph(new RecognizeTagNode { Id = "r", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1) });

        await Assert.That(Var(graph, "tag").IsRead).IsFalse();
    }

    [Test]
    public async Task SeveralReadersOfOneVariableAreAllListed()
    {
        var graph = Graph(
            new RecognizeTagNode { Id = "r", TemplateSet = "classes", Region = new ScreenRect(0, 0, 1, 1), Matched = "a" },
            new AddTagNode { Id = "a", Tag = "{tag}-готов", Next = "i" },
            new SetIconNode { Id = "i", IconPath = "{tag}.png" });

        await Assert.That(Var(graph, "tag").Reads.Select(r => r.NodeId)).IsEquivalentTo(new[] { "a", "i" });
    }

    [Test]
    public async Task TwoPlaceholdersInOneStringAreTwoVariables()
    {
        var graph = Graph(new SetIconNode { Id = "i", IconPath = "{dir}/{tag}.png" });

        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{dir}/{tag}.png"))
            .IsEquivalentTo(new[] { "dir", "tag" });
        await Assert.That(Var(graph, "dir").Reads).Count().IsEqualTo(1);
        await Assert.That(Var(graph, "tag").Reads).Count().IsEqualTo(1);
    }

    [Test]
    public async Task TheSameVariableTwiceInOneStringIsOneRead()
    {
        // Otherwise the panel would say «читает set-icon, set-icon».
        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{tag}/{tag}.png"))
            .IsEquivalentTo(new[] { "tag" });
    }

    [Test]
    public async Task BlankVariableNamesAreIgnored()
    {
        var graph = Graph(
            new RecognizeTagNode { Id = "r", TemplateSet = "s", Region = new ScreenRect(0, 0, 1, 1), ResultVar = "  " },
            new ClickNode { Id = "c", PointVar = string.Empty });

        await Assert.That(MacroVariableAnalysis.Analyze(graph).Select(v => v.Name))
            .IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    public async Task ThePlaceholderSyntaxIsTheOneTheExecutorSubstitutes()
    {
        // Same regex object, so this is really asserting that nobody has re-typed it. The
        // pairing matters more than the individual cases: a panel claiming a node reads a
        // variable the executor never substitutes is the D4 targets-badge lie all over again.
        var variables = new MacroVariables();
        variables.Set("tag", "Жрец");

        await Assert.That(variables.Interpolate("{tag}.png")).IsEqualTo("Жрец.png");
        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{tag}.png")).IsEquivalentTo(new[] { "tag" });

        // There is no escaping, so a doubled brace is NOT a literal one: both sides see the
        // inner {tag} and leave the outer braces alone. Pinned because "{{" looks like an
        // escape to anyone who has met string.Format, and the panel must report what the
        // executor will actually do rather than what the author hoped.
        await Assert.That(variables.Interpolate("{{tag}}")).IsEqualTo("{Жрец}");
        await Assert.That(MacroVariableAnalysis.PlaceholdersIn("{{tag}}")).IsEquivalentTo(new[] { "tag" });
    }
}
