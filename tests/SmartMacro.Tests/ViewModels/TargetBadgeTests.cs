using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Resources;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// D4, макет 1g: «8 окон · кроме Склад».
//
// Бейдж считается в ПАНЕЛИ — никакого нового запроса IPC, — но считается по
// TargetSelector.Matches, то есть по тому же правилу, которое зовёт SelectorEvaluator у демона.
// Именно это свойство эти тесты и защищают: бейдж, расходящийся с тем, кого исполнитель на самом
// деле берёт в цель, хуже, чем никакого бейджа вовсе, — ведь он и висит на экране ровно затем,
// чтобы сказать, кого заденет прогон.
public class TargetBadgeTests
{
    /// <summary>Номера форм множественного числа — то, чем [Arguments] умеет быть.</summary>
    private const int One = 0;

    private const int Few = 1;
    private const int Many = 2;

    private static WindowCatalog Catalog(params (long Hwnd, string[] Tags)[] windows)
    {
        var catalog = new WindowCatalog();
        catalog.Reset([.. windows.Select(w => new WindowDto(w.Hwnd, "elementclient_64", w.Tags))]);
        return catalog;
    }

    /// <summary>Одиннадцать окон: восемь «перс», один «Склад» и два вовсе без тегов.</summary>
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

    // ---- четыре свёрнутых состояния ----------------------------------------------------

    [Test]
    public async Task ContextWindow_IsTheNeutralPill()
    {
        var vm = TargetSelectorViewModel.FromSelector(null);
        vm.Windows = Party();

        await Assert.That(vm.BadgeText).IsEqualTo(Strings.Editor_Targets_CountContext);
        await Assert.That(vm.BadgeIsAccent).IsFalse();
        await Assert.That(vm.BadgeIsDanger).IsFalse();
        await Assert.That(vm.ShowsHollowDot).IsTrue();
        await Assert.That(vm.ShowsBars).IsFalse();
    }

    [Test]
    public async Task Exclusion_ReadsAsTheMockupDoes()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        // Собранная строка: проверяются ЧАСТИ, которые несёт логика, — счёт, требуемый тег и
        // исключённый, — и то, что выбрана форма «требование И исключение», а не соседние.
        var badge = Msg.Args(vm.BadgeText, Strings.Editor_Targets_CountRequireExclude);
        await Assert.That(Msg.Arg(badge[0], Strings.Editor_Targets_WindowCount_Many)).IsEqualTo("8");
        await Assert.That(badge[1]).IsEqualTo("перс");
        await Assert.That(badge[2]).IsEqualTo("Склад");
        await Assert.That(vm.BadgeIsAccent).IsTrue();
        await Assert.That(vm.MatchCount).IsEqualTo(8);
        await Assert.That(vm.TotalCount).IsEqualTo(11);
        await Assert.That(Msg.Args(vm.HitText, Strings.Editor_Targets_HitsCount))
            .IsEquivalentTo(new[] { "8", "11" });
    }

    [Test]
    public async Task ZeroMatches_IsAnErrorState_NotANeutralZero()
    {
        var vm = Selector(Party(), require: "Инквизитор");

        var badge = Msg.Args(vm.BadgeText, Strings.Editor_Targets_CountRequire);
        await Assert.That(Msg.Arg(badge[0], Strings.Editor_Targets_WindowCount_Many)).IsEqualTo("0");
        await Assert.That(badge[1]).IsEqualTo("Инквизитор");
        await Assert.That(vm.BadgeIsDanger).IsTrue();
        await Assert.That(vm.BadgeIsAccent).IsFalse();
        await Assert.That(vm.ShowsDangerDot).IsTrue();
        await Assert.That(vm.ShowsBars).IsFalse();
    }

    // При закрытой игре КАЖДЫЙ селектор не совпадает ни с чем. Покрасив каждую ноду каждого
    // макроса красным, мы приучили бы пользователя не замечать цвет, который должен означать
    // «этот селектор неверен», — поэтому у пустого демона своё, тихое состояние.
    [Test]
    public async Task NoWindowsAtAll_IsQuiet_NotAnError()
    {
        var vm = Selector(Catalog(), require: "перс");

        // Тихое состояние — это ВЫБОР другого ключа, а не другого цвета при том же тексте.
        await Assert.That(vm.BadgeText).IsEqualTo(Strings.Editor_Targets_CountNoWindows);
        await Assert.That(vm.BadgeIsDanger).IsFalse();
        await Assert.That(vm.BadgeIsAccent).IsFalse();
        await Assert.That(vm.HitText).IsEqualTo(Strings.Editor_Targets_HitsNoWindows);
    }

    [Test]
    public async Task NoCatalogueAttached_ReportsNothingRatherThanInventingANumber()
    {
        var vm = Selector(null, require: "перс");

        await Assert.That(vm.TotalCount).IsEqualTo(0);
        await Assert.That(vm.BadgeText).IsEqualTo(Strings.Editor_Targets_CountNoWindows);
    }

    // окно / окна / окон, включая исключение для 11–14.
    //
    // Ожидание — НОМЕР ФОРМЫ, а не готовая строка: проверять здесь надо, что при 1/2/5 выбраны
    // разные ключи, а какими словами они записаны — дело вычитки. Номером, а не Strings.X, потому
    // что [Arguments] принимает только константы времени компиляции.
    [Test]
    [Arguments(1, One)]
    [Arguments(2, Few)]
    [Arguments(5, Many)]
    [Arguments(11, Many)]
    [Arguments(21, One)]
    public async Task WindowCount_IsPluralisedInRussian(int count, int expectedForm)
    {
        string[] forms =
        [
            Strings.Editor_Targets_WindowCount_One,
            Strings.Editor_Targets_WindowCount_Few,
            Strings.Editor_Targets_WindowCount_Many,
        ];
        var windows = Enumerable.Range(0, count).Select(i => ((long)(0x200000 + i), new[] { "перс" })).ToArray();
        // Селектор с обоими пустыми списками означает «каждое окно», и счётчик говорит это сам по
        // себе, — так что бейдж здесь не более чем число, и именно это тут и меряется.
        var vm = Selector(Catalog(windows));

        // Выбрана эта форма, и в неё подставлено само число…
        await Assert.That(Msg.Arg(vm.BadgeText, forms[expectedForm]))
            .IsEqualTo(count.ToString(CultureInfo.CurrentCulture));

        // …и ни одна из двух других. Без этой половины «согласование» не проверяется вовсе:
        // «1 окон» тоже подставляет число.
        for (var form = 0; form < forms.Length; form++)
        {
            if (form != expectedForm)
            {
                await Assert.That(Msg.Is(vm.BadgeText, forms[form])).IsFalse();
            }
        }
    }

    // Коробке на канве достаётся счётчик без тегов: в заголовок шириной 210px уже уложены значок
    // семейства и подпись типа, а полная строка выдавливает эту подпись целиком.
    [Test]
    public async Task CompactBadge_KeepsTheCountAndDropsTheTags()
    {
        // Совпадение с ГОЛОЙ формой счёта доказывает и число, и то, что тегов в строке нет:
        // «8 окон · перс · кроме Склад» под формат «{0} окон» не подходит.
        await Assert.That(Msg.Arg(
            Selector(Party(), require: "перс", exclude: "Склад").BadgeCountText,
            Strings.Editor_Targets_WindowCount_Many)).IsEqualTo("8");
        await Assert.That(Msg.Arg(
            Selector(Party(), require: "Инквизитор").BadgeCountText,
            Strings.Editor_Targets_WindowCount_Many)).IsEqualTo("0");
        await Assert.That(Selector(Catalog(), require: "перс").BadgeCountText)
            .IsEqualTo(Strings.Editor_Targets_CountNoWindows);
        await Assert.That(TargetSelectorViewModel.FromSelector(null).BadgeCountText)
            .IsEqualTo(Strings.Editor_Targets_CompactContext);
    }

    // ---- полоски доли ----------------------------------------------------------------------

    [Test]
    public async Task Bars_ShowTheShare_AndNeverRoundANonZeroShareDownToNothing()
    {
        var eightOfEleven = Selector(Party(), require: "перс", exclude: "Склад");
        var oneOfEleven = Selector(Party(), require: "Лучник");

        // 8/11 → 2,9 → три полоски, ровно так, как рисует макет.
        await Assert.That(eightOfEleven.Bars).IsEquivalentTo(new[] { true, true, true, false });
        // 1/11 → 0,36, что округляется в ничто; пустая полоска рядом с «1 окно» читалась бы как
        // ноль, поэтому любое попадание стоит одной полоски.
        await Assert.That(oneOfEleven.Bars).IsEquivalentTo(new[] { true, false, false, false });
    }

    // ---- развёрнутый вид -------------------------------------------------------------------

    [Test]
    public async Task Expansion_NamesTheHits_StrikesOutTheMisses_AndCountsTheUntagged()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        // Четверо названы по именам, остальные свёрнуты в "+N".
        await Assert.That(vm.HitWindows).Count().IsEqualTo(4);
        await Assert.That(vm.HitWindows[0].Label).IsEqualTo("0x140000 перс Лучник");
        await Assert.That(vm.HitWindows[0].IsExcluded).IsFalse();
        await Assert.That(vm.MoreText).IsEqualTo("+4");
        await Assert.That(vm.HasMore).IsTrue();

        // Окно «Склад» показано зачёркнутым: кто именно выпал — это половина всего смысла.
        await Assert.That(vm.MissedWindows.Select(w => w.Label)).Contains("0x140550 перс Склад");
        await Assert.That(vm.MissedWindows.All(w => w.IsExcluded)).IsTrue();

        // До окон без тегов маршрут не проложить вовсе, поэтому их считают, а не называют.
        await Assert.That(Msg.Arg(vm.UntaggedText, Strings.Editor_Targets_Untagged)).IsEqualTo("2");
        await Assert.That(vm.HasUntagged).IsTrue();
    }

    // ---- правило общее, а не переписанное заново ---------------------------------------------

    // Селекторы панель вычисляет сама (запроса IPC нет), поэтому ЕДИНСТВЕННОЕ, что обязано
    // выполняться, — что вычисляет она их правилом движка. Каждое сочетание ниже проходит через
    // TargetSelector.Matches с обеих сторон.
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

    // ---- обновления на лету --------------------------------------------------------------------

    [Test]
    public async Task TaggingAWindow_MovesTheCountWithoutARoundTrip()
    {
        var catalog = Catalog((0x1, ["перс"]), (0x2, []));
        var vm = Selector(catalog, require: "перс");
        await Assert.That(vm.MatchCount).IsEqualTo(1);

        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(vm.BadgeText) ? 1 : 0;

        // Вот что делает пуш WindowTagsChanged.
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

    // ---- редактор фишек во всплывающем окне -------------------------------------------------------

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
        // Редактор фишек пишет насквозь в модель, поэтому сохранённый граф выходит той же формы,
        // как если бы тег набрали в поле инспектора.
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
        // Отклонённая фиксация поле не трогает — так пользователь видит, что именно
        // проигнорировали.
        await Assert.That(vm.NewRequireTag).IsEqualTo("перс");
    }

    // Выключить селектор — это НЕ то же самое, что очистить поля: разница между «нет значения» и
    // «пусто» — это разница между «контекстное окно» и «каждое окно», и теги обязаны пережить круг
    // через кнопки режима во всплывающем окне.
    [Test]
    public async Task SwitchingToContextAndBack_KeepsTheTags()
    {
        var vm = Selector(Party(), require: "перс", exclude: "Склад");

        vm.UseSelector = false;
        await Assert.That(vm.ToSelector()).IsNull();
        await Assert.That(vm.BadgeText).IsEqualTo(Strings.Editor_Targets_CountContext);

        vm.UseSelector = true;
        await Assert.That(vm.ToSelector()!.RequireTags).IsEquivalentTo(new List<string> { "перс" });
        // Теги пережили круг — это видно по тому, что оба вернулись в бейдж.
        await Assert.That(Msg.Args(vm.BadgeText, Strings.Editor_Targets_CountRequireExclude).Skip(1))
            .IsEquivalentTo(new[] { "перс", "Склад" });
    }

    // ---- половина, за которую отвечает редактор -----------------------------------------------

    // Каталог принадлежит редактору, и он раздаёт его каждой подключаемой ноде так же, как
    // раздаёт NodeChoices. Без этого бейдж на только что открытом графе висел бы на «нет окон»,
    // пока его не тронет что-нибудь ещё.
    [Test]
    public async Task Editor_SeedsTheCatalogue_AndAttachesItToEveryNode()
    {
        var client = new FakeIpcClient();
        client.Respond(IpcMessageTypes.GetRunningMacros, _ => Array.Empty<RunningMacroDto>());
        client.Respond(IpcMessageTypes.GetWindows, _ => new[]
        {
            new WindowDto(0x1, "elementclient_64", ["перс", "Лучник"]),
            new WindowDto(0x2, "elementclient_64", ["перс", "Склад"]),
        });

        using var editor = new MacroEditorViewModel(
            client, TempLibrary.Shared, null, null, ImmediateUiDispatcher.Instance);
        editor.LoadGraph(new MacroGraph
        {
            Name = "тест",
            StartNodeId = Ids.Of("k"),
            Nodes =
            [
                new KeyPressNode
                {
                    Id = Ids.Of("k"), DisplayName = "k", Key = VirtualKey.A,
                    Target = new TargetSelector { ExcludeTags = ["Склад"] }
                }
            ],
        });

        var target = editor.Nodes.Single().Target!;
        await Assert.That(editor.Windows.Count).IsEqualTo(2);
        await Assert.That(target.TotalCount).IsEqualTo(2);
        var seeded = Msg.Args(target.BadgeText, Strings.Editor_Targets_CountExclude);
        await Assert.That(Msg.Arg(seeded[0], Strings.Editor_Targets_WindowCount_One)).IsEqualTo("1");
        await Assert.That(seeded[1]).IsEqualTo("Склад");

        // …и он следует за пушами демона.
        client.RaiseEvent(IpcMessageTypes.WindowTagsChanged, new WindowDto(0x2, "elementclient_64", ["перс"]));
        await Assert.That(target.MatchCount).IsEqualTo(2);

        client.RaiseEvent(IpcMessageTypes.WindowClosed, new WindowClosedEvent(0x1));
        await Assert.That(target.TotalCount).IsEqualTo(1);
    }
}
