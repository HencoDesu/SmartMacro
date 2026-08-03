using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D4, mockup 1g: «8 окон · кроме Склад».
//
// The badge is computed in the PANEL — no new IPC request — but against
// TargetSelector.Matches, the same rule the daemon's SelectorEvaluator calls. That is the
// property these tests exist to protect: a badge that disagrees with what the executor
// actually targets is worse than no badge at all, because telling you what a run will hit
// is the whole reason it is on screen.
public class TargetBadgeTests
{
    private static WindowCatalog Catalog(params (long Hwnd, string[] Tags)[] windows)
    {
        var catalog = new WindowCatalog();
        catalog.Reset([.. windows.Select(w => new WindowDto(w.Hwnd, "elementclient_64", w.Tags))]);
        return catalog;
    }

    /// <summary>Eleven windows: eight «перс», one «Склад», two with no tags at all.</summary>
    private static WindowCatalog Party()
    {
        var windows = new List<(long, string[])>();
        for (var i = 0; i < 8; i++)
        {
            windows.Add((0x140000 + i, ["перс", i == 0 ? "Лучник" : "Жрец"]));
        }
        windows.Add((0x140550, ["перс", "Склад"]));
        windows.Add((0x140900, []));
        windows.Add((0x140901, []));
        return Catalog([.. windows]);
    }

    private static TargetSelectorViewModel Selector(WindowCatalog? catalog, string require = "", string exclude = "")
    {
        var vm = TargetSelectorViewModel.FromSelector(new TargetSelector
        {
            RequireTags = [.. require.Split(',', StringSplitOptions.RemoveEmptyEntries)],
            ExcludeTags = [.. exclude.Split(',', StringSplitOptions.RemoveEmptyEntries)],
        });
        vm.Windows = catalog;
        return vm;
    }

    // ---- the four collapsed states -----------------------------------------------------

    [Test]
    public async Task ContextWindow_IsTheNeutralPill()
    {
        var vm = TargetSelectorViewModel.FromSelector(null);
        vm.Windows = Party();

        await Assert.That(vm.BadgeText).IsEqualTo("1 окно · контекст");
        await Assert.That(vm.BadgeIsAccent).IsFalse();
        await Assert.That(vm.BadgeIsDanger).IsFalse();
        await Assert.That(vm.ShowsHollowDot).IsTrue();
        await Assert.That(vm.ShowsBars).IsFalse();
    }

    [Test]
    public async Task Exclusion_ReadsAsTheMockupDoes()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        await Assert.That(vm.BadgeText).IsEqualTo("8 окон · перс · кроме Склад");
        await Assert.That(vm.BadgeIsAccent).IsTrue();
        await Assert.That(vm.MatchCount).IsEqualTo(8);
        await Assert.That(vm.TotalCount).IsEqualTo(11);
        await Assert.That(vm.HitText).IsEqualTo("8 из 11");
    }

    [Test]
    public async Task ZeroMatches_IsAnErrorState_NotANeutralZero()
    {
        var vm = Selector(Party(), require: "Инквизитор");

        await Assert.That(vm.BadgeText).IsEqualTo("0 окон · Инквизитор");
        await Assert.That(vm.BadgeIsDanger).IsTrue();
        await Assert.That(vm.BadgeIsAccent).IsFalse();
        await Assert.That(vm.ShowsDangerDot).IsTrue();
        await Assert.That(vm.ShowsBars).IsFalse();
    }

    // With the game closed EVERY selector matches nothing. Painting every node of every
    // macro red would train the user to ignore the colour that is supposed to mean "this
    // selector is wrong", so the empty daemon gets its own quiet state.
    [Test]
    public async Task NoWindowsAtAll_IsQuiet_NotAnError()
    {
        var vm = Selector(Catalog(), require: "перс");

        await Assert.That(vm.BadgeText).IsEqualTo("нет окон");
        await Assert.That(vm.BadgeIsDanger).IsFalse();
        await Assert.That(vm.BadgeIsAccent).IsFalse();
        await Assert.That(vm.HitText).IsEqualTo("нет окон под управлением");
    }

    [Test]
    public async Task NoCatalogueAttached_ReportsNothingRatherThanInventingANumber()
    {
        var vm = Selector(null, require: "перс");

        await Assert.That(vm.TotalCount).IsEqualTo(0);
        await Assert.That(vm.BadgeText).IsEqualTo("нет окон");
    }

    // окно / окна / окон, including the 11–14 exception.
    [Test]
    [Arguments(1, "1 окно")]
    [Arguments(2, "2 окна")]
    [Arguments(5, "5 окон")]
    [Arguments(11, "11 окон")]
    [Arguments(21, "21 окно")]
    public async Task WindowCount_IsPluralisedInRussian(int count, string expected)
    {
        var windows = Enumerable.Range(0, count).Select(i => ((long)(0x200000 + i), new[] { "перс" })).ToArray();
        // A selector with both lists empty means "every window", and the count says that on
        // its own — so the badge is nothing but the number, which is what this is measuring.
        var vm = Selector(Catalog(windows));

        await Assert.That(vm.BadgeText).IsEqualTo(expected);
    }

    // The canvas box gets the count without the tags: a 210px header already carries a
    // family glyph and a type label, and the full string pushes the label out entirely.
    [Test]
    public async Task CompactBadge_KeepsTheCountAndDropsTheTags()
    {
        await Assert.That(Selector(Party(), require: "перс", exclude: "Склад").BadgeCountText).IsEqualTo("8 окон");
        await Assert.That(Selector(Party(), require: "Инквизитор").BadgeCountText).IsEqualTo("0 окон");
        await Assert.That(Selector(Catalog(), require: "перс").BadgeCountText).IsEqualTo("нет окон");
        await Assert.That(TargetSelectorViewModel.FromSelector(null).BadgeCountText).IsEqualTo("контекст");
    }

    // ---- share bars ----------------------------------------------------------------------

    [Test]
    public async Task Bars_ShowTheShare_AndNeverRoundANonZeroShareDownToNothing()
    {
        var eightOfEleven = Selector(Party(), require: "перс", exclude: "Склад");
        var oneOfEleven = Selector(Party(), require: "Лучник");

        // 8/11 → 2.9 → three bars, exactly as the mockup draws it.
        await Assert.That(eightOfEleven.Bars).IsEquivalentTo(new[] { true, true, true, false });
        // 1/11 → 0.36, which rounds to nothing; an empty strip beside "1 окно" would read
        // as zero, so any hit is worth a bar.
        await Assert.That(oneOfEleven.Bars).IsEquivalentTo(new[] { true, false, false, false });
    }

    // ---- the expansion --------------------------------------------------------------------

    [Test]
    public async Task Expansion_NamesTheHits_StrikesOutTheMisses_AndCountsTheUntagged()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        // Four named, the rest collapsed into "+N".
        await Assert.That(vm.HitWindows).Count().IsEqualTo(4);
        await Assert.That(vm.HitWindows[0].Label).IsEqualTo("0x140000 перс Лучник");
        await Assert.That(vm.HitWindows[0].IsExcluded).IsFalse();
        await Assert.That(vm.MoreText).IsEqualTo("+4");
        await Assert.That(vm.HasMore).IsTrue();

        // The «Склад» window is shown, struck through — who fell out is half the point.
        await Assert.That(vm.MissedWindows.Select(w => w.Label)).Contains("0x140550 перс Склад");
        await Assert.That(vm.MissedWindows.All(w => w.IsExcluded)).IsTrue();

        // Windows with no tags cannot be routed to at all, so they are counted, not named.
        await Assert.That(vm.UntaggedText).IsEqualTo("2 без тегов");
        await Assert.That(vm.HasUntagged).IsTrue();
    }

    // ---- the rule is shared, not reimplemented ---------------------------------------------

    // The panel evaluates selectors itself (no IPC request), so the ONE thing that must hold
    // is that it evaluates them with the engine's rule. Every combination below goes through
    // TargetSelector.Matches from both directions.
    [Test]
    public async Task BadgeCount_AgreesWithTheRuleTheExecutorUses()
    {
        var catalog = Party();
        TargetSelector[] cases =
        [
            new(),
            new() { RequireTags = ["перс"] },
            new() { RequireTags = ["перс"], ExcludeTags = ["Склад"] },
            new() { ExcludeTags = ["Склад"] },
            new() { RequireTags = ["перс", "Жрец"] },
            new() { RequireTags = ["ПЕРС"] },
        ];

        foreach (var selector in cases)
        {
            var vm = TargetSelectorViewModel.FromSelector(selector);
            vm.Windows = catalog;
            var expected = catalog.Windows.Count(window => selector.Matches(window.Tags));

            await Assert.That(vm.MatchCount).IsEqualTo(expected);
        }
    }

    // ---- live updates ------------------------------------------------------------------------

    [Test]
    public async Task TaggingAWindow_MovesTheCountWithoutARoundTrip()
    {
        var catalog = Catalog((0x1, ["перс"]), (0x2, []));
        var vm = Selector(catalog, require: "перс");
        await Assert.That(vm.MatchCount).IsEqualTo(1);

        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(vm.BadgeText) ? 1 : 0;

        // What a WindowTagsChanged push does.
        catalog.Upsert(new WindowDto(0x2, "elementclient_64", ["перс"]));

        await Assert.That(vm.MatchCount).IsEqualTo(2);
        await Assert.That(raised).IsGreaterThan(0);

        catalog.Remove(0x1);
        await Assert.That(vm.MatchCount).IsEqualTo(1);
        await Assert.That(vm.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task DetachingTheCatalogue_StopsTheBadgeListening()
    {
        var catalog = Catalog((0x1, ["перс"]));
        var vm = Selector(catalog, require: "перс");

        vm.Windows = null;
        catalog.Upsert(new WindowDto(0x2, "elementclient_64", ["перс"]));

        await Assert.That(vm.TotalCount).IsEqualTo(0);
    }

    // ---- the popup's chip editor ---------------------------------------------------------------

    [Test]
    public async Task TagChips_MirrorTheCommaSeparatedText_BothWays()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        await Assert.That(vm.RequireChips.Select(c => c.Text)).IsEquivalentTo(new[] { "перс" });
        await Assert.That(vm.ExcludeChips.Select(c => c.Text)).IsEquivalentTo(new[] { "Склад" });

        vm.NewExcludeTag = "Инквизитор";
        vm.CommitExcludeTag();

        await Assert.That(vm.ExcludeText).IsEqualTo("Склад, Инквизитор");
        await Assert.That(vm.NewExcludeTag).IsEqualTo(string.Empty);
        // The chip editor writes through to the model, so the saved graph is the same shape
        // as if the tag had been typed into the inspector's box.
        await Assert.That(vm.ToSelector()!.ExcludeTags).IsEquivalentTo(new List<string> { "Склад", "Инквизитор" });

        vm.ExcludeChips.First(c => c.Text == "Склад").Remove();
        await Assert.That(vm.ExcludeText).IsEqualTo("Инквизитор");
    }

    [Test]
    public async Task CommittingABlankOrDuplicateTag_ChangesNothing()
    {
        var vm = Selector(Party(), require: "перс");

        vm.NewRequireTag = "   ";
        vm.CommitRequireTag();
        await Assert.That(vm.RequireText).IsEqualTo("перс");

        vm.NewRequireTag = "перс";
        vm.CommitRequireTag();
        await Assert.That(vm.RequireText).IsEqualTo("перс");
        // A rejected commit leaves the box alone, so the user can see what was ignored.
        await Assert.That(vm.NewRequireTag).IsEqualTo("перс");
    }

    // Turning the selector off is NOT the same as clearing the boxes: null vs empty is the
    // difference between "the context window" and "every window", and the tags have to
    // survive the round trip through the popup's mode buttons.
    [Test]
    public async Task SwitchingToContextAndBack_KeepsTheTags()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        vm.UseSelector = false;
        await Assert.That(vm.ToSelector()).IsNull();
        await Assert.That(vm.BadgeText).IsEqualTo("1 окно · контекст");

        vm.UseSelector = true;
        await Assert.That(vm.ToSelector()!.RequireTags).IsEquivalentTo(new List<string> { "перс" });
        await Assert.That(vm.BadgeText).IsEqualTo("8 окон · перс · кроме Склад");
    }

    // ---- the editor's end of it ------------------------------------------------------------

    // The editor owns the catalogue and hands it to every node it attaches, the same way it
    // hands out NodeIdChoices. Without this the badge on a freshly opened graph would sit at
    // "нет окон" until something else touched it.
    [Test]
    public async Task Editor_SeedsTheCatalogue_AndAttachesItToEveryNode()
    {
        var client = new FakeIpcClient();
        client.Respond(IpcMessageTypes.GetMacros, _ => Array.Empty<MacroGraph>());
        client.Respond(IpcMessageTypes.GetRunningMacros, _ => Array.Empty<RunningMacroDto>());
        client.Respond(IpcMessageTypes.GetWindows, _ => new[]
        {
            new WindowDto(0x1, "elementclient_64", ["перс", "Лучник"]),
            new WindowDto(0x2, "elementclient_64", ["перс", "Склад"]),
        });

        using var editor = new MacroEditorViewModel(client, null, null, ImmediateUiDispatcher.Instance, @"C:\m");
        editor.LoadGraph(new MacroGraph
        {
            Name = "тест",
            StartNodeId = "k",
            Nodes = [new KeyPressNode { Id = "k", Key = VirtualKey.A, Target = new TargetSelector { ExcludeTags = ["Склад"] } }],
        });

        var target = editor.Nodes.Single().Target!;
        await Assert.That(editor.Windows.Count).IsEqualTo(2);
        await Assert.That(target.TotalCount).IsEqualTo(2);
        await Assert.That(target.BadgeText).IsEqualTo("1 окно · кроме Склад");

        // …and it follows the daemon's pushes.
        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged, new WindowDto(0x2, "elementclient_64", ["перс"]));
        await Assert.That(target.MatchCount).IsEqualTo(2);

        client.RaiseEvent(IpcMessageTypes.WindowClosed, new WindowClosedEvent(0x1));
        await Assert.That(target.TotalCount).IsEqualTo(1);
    }
}
